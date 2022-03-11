#!/usr/bin/python3
import os, sys, subprocess, json, click, paramiko, time, functools, platform, shlex, shutil, requests, uuid
from collections import namedtuple

print = functools.partial(print, flush=True)

libs = ['google-auth-library-python', 'cachetools/src']

for l in libs:
    sys.path.insert(0, os.path.dirname(os.path.realpath(__file__))+f'/../extra_python_deps/{l}')

import google.auth
from google.auth.transport.requests import AuthorizedSession

USER = 'scalerunner'
PUBKEY = os.path.join(os.path.expanduser('~'), '.ssh/id_rsa.pub'), f'/home/{USER}/.ssh/authorized_keys'
SARGRAPH = os.path.realpath('../sargraph/sargraph.py'), f'/home/{USER}/sargraph.py'
GCLOUD = shutil.which('gcloud')
PREEMPT = 'true'
GH_ENV_LIST = ["GITHUB_JOB_FULL", "GITHUB_SHA", "GITHUB_RUN_ID"]

LABELS = [{e.lower(): (os.environ.get(e) or 'null')[:63].lower() for e in GH_ENV_LIST}]

def load_config():
    with open('../.vm_specs.json', 'r') as f:
        return json.load(f, object_hook=lambda d: namedtuple('vm_specs', d.keys())(*d.values()))

CONFIG = load_config()

def wait_for_gcp(authed_session, link):
    start = time.time()
    # set timeout to 60s
    while elapsed(start) < 60:
        r = authed_session.get(link)
        result = json.loads(r.text)

        if "status" not in result:
            print("Unexpected output while waiting for response! Exiting!")
            sys.exit(1)

        if result['status'] == 'DONE':
            if 'error' in result:
                print(f"Error occured while processing request: {result['error']}")
                sys.exit(1)
            return result

        time.sleep(1)
    print("Timeout while waiting for response! Exiting!")
    sys.exit(1)

def export_gcp_ip(authed_session, link, runner_name):
    r = authed_session.get(link)
    result = json.loads(r.text)
    if "networkInterfaces" not in result or len(result['networkInterfaces']) < 0 or "networkIP" not in result['networkInterfaces'][0]:
        print("Unexpected output while processing response! Exiting!")
        sys.exit(1)
    ip = result['networkInterfaces'][0]['networkIP']
    os.environ[runner_name] = ip
    # Environment variables doesn't get exported
    # to the parent process, this export is only for
    # current script, later runner parses below output
    # and sets correct ip in the parent process
    print(f"export {runner_name}={ip}")
    return

def describe_instance(authed_session, id):
    URL = f"https://compute.googleapis.com/compute/v1/projects/{CONFIG.gcp.project}/zones/{CONFIG.gcp.zone}/instances/{id}"
    r = authed_session.get(URL)
    result = json.loads(r.text)
    if "machineType" not in result:
        print("Unexpected output while processing response! Exiting!")
        sys.exit(1)
    return result['machineType']

def create_instance_call(authed_session, instance_number, instance_name, key, boot_disk_name, external_disk_info, preemptible_machine, machine_type, uuid):
    URL = f"https://compute.googleapis.com/compute/v1/projects/{CONFIG.gcp.project}/zones/{CONFIG.gcp.zone}/instances?requestId={uuid}"
    data = {
        "name": f"{instance_name}",
        "machineType": f"zones/{CONFIG.gcp.zone}/machineTypes/{machine_type}",
        "networkInterfaces": [{
            "subnetwork": f"regions/{CONFIG.gcp.zone.rsplit('-', 1)[0]}/subnetworks/{CONFIG.gcp.subnet}",
        }],
        "metadata": {
            "items": [
                {
                    "key": "serial-port-enable",
                    "value": "true",
                },
                {
                    "key": "ssh-key",
                    "value": f"coordinator:{key}",
                },
            ],
        },
        "scheduling": {
            "automaticRestart": "false",
            "onHostMaintenance": "TERMINATE",
            "preemptible": f"{preemptible_machine}",
        },
        "tags": {
            "items": [
                "runners"
            ],
        },
        "disks": [{
            "type": f"{CONFIG.gcp.disk_type}",
            "boot": "true",
            "autoDelete": "true",
            "deviceName": f"{boot_disk_name}",
            "initializeParams": {
                "diskSizeGb": f"{CONFIG.machine.disk}",
                "sourceImage": f"projects/{CONFIG.gcp.project}/global/images/{CONFIG.gcp.image}",
            },
        },
            external_disk_info
        ],
        "reservationAffinity": {
            "consumeReservationType": "any"
        },
        "labels": LABELS,
    }
    r = authed_session.post(url=URL, json=data)
    return json.loads(r.text)

def create_instance(authed_session, instance_number, instance_name, key, boot_disk_name, external_disk_info, preemptible_machine, machine_type):
    success = False
    retries = 5
    request_uuid = str(uuid.uuid4())
    while retries > 0:
        retries = retries - 1
        # Same UUID makes sure that we won't create multiple VMs
        result = create_instance_call(authed_session, instance_number, instance_name, key, boot_disk_name, external_disk_info, preemptible_machine, machine_type, request_uuid)
        if "selfLink" not in result or "targetLink" not in result:
            print("Unexpected response when creating VM!")
            continue
        wait_for_gcp(authed_session, result['selfLink'])
        export_gcp_ip(authed_session, result['targetLink'], instance_name)
        success = True
        break
    if success is False:
        print("Couldn't create instance: {instance_name}! Exiting!")
        sys.exit(1)

def elapsed(start):
    return round(time.time() - start, 2)

def get_gcp_disk(authed_session, project, zone, disk_name):
    if not disk_name or not zone:
        return None

    URL = "https://compute.googleapis.com/compute/v1/projects/{project}/zones/{zone}/disks/{resourceId}"


    r = authed_session.get(URL.format(project=project, zone=zone, resourceId=disk_name))
    return json.loads(r.text)

def check_machine_type(machine_type):
    if machine_type is None:
        print("Machine type is None! Please check your configuration, exiting!")
        sys.exit(1)
    try:
        allow_list = CONFIG.gcp.allowed_machine_types
    except AttributeError:
        # We have old config without this field, assume only CONFIG.gcp.type is in allow list
        allow_list = [CONFIG.gcp.type]

    if machine_type not in allow_list:
        print(f"Requested machine type {machine_type} was not found in the allow list! Please use a different machine type. Exiting!")
        sys.exit(1)

def create_ssh_connection(target):
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.client.AutoAddPolicy())

    ssh_timeout = ssh_timeout_c = 50

    while ssh_timeout_c > 0:
        try:
            ssh.connect(
                    target,
                    username=USER,
                    password=USER,
                    timeout=1,
                    auth_timeout=1,
                    banner_timeout=1,
            )
            break
        except Exception as e:
            if ssh_timeout_c % 10 == 0:
                print('Waiting for SSH... [{}/{}] '.format((int)(((ssh_timeout - ssh_timeout_c) / 10) + 1), (int)(ssh_timeout / 10)))
            ssh_timeout_c -= 1

            if ssh_timeout_c == 0:
                print('Timeout while waiting for SSH!')
                print(e)
                sys.exit(1)

            time.sleep(1)
    return ssh

def execute_ssh_commands(ssh, commands):
    for cmd in commands:
        _, stdout, stderr = ssh.exec_command(cmd)

        stdout_lines = stdout.readlines()
        stderr_lines = stderr.readlines()

        for l in stdout_lines:
            print(l.strip())

        for l in stderr_lines:
            print(l.strip())

def get_instance_name(instance_number):
    return f'{platform.node()}-auto-spawned{instance_number}'

def delete_instance_call(authed_session, instance_name, uuid):
    URL = f"https://compute.googleapis.com/compute/v1/projects/{CONFIG.gcp.project}/zones/{CONFIG.gcp.zone}/instances/{instance_name}?requestID={uuid}"
    r = authed_session.delete(URL)
    return json.loads(r.text)

def delete_instance(authed_session, instance_name):
    success = False
    retries = 5
    request_uuid = str(uuid.uuid4())
    while retries > 0:
        retries = retries - 1
        # Same UUID makes sure that we won't delete multiple VMs
        result = delete_instance_call(authed_session, instance_name, request_uuid)
        if "selfLink" not in result:
            print("Unexpected output while processing response!")
            continue
        wait_for_gcp(authed_session, result['selfLink'])
        success = True
        break
    if success is False:
        print("Couldn't delete instance: {instance_name}! Exiting!")
        sys.exit(1)

def create_vm(instance_number, container_file, disk_name=None, preemptible_override=None, machine_type=None):
    print("Attempting to spawn a machine..")
    if machine_type is None:
        machine_type = CONFIG.gcp.type
    check_machine_type(machine_type)
    instance_name = get_instance_name(instance_number)
    credentials, _ = google.auth.default()
    authed_session = AuthorizedSession(credentials)
    print(f"Using coordinator machine: {describe_instance(authed_session, platform.node()).rsplit('/', 1)[-1]}")
    print(f'Spawning a GCP machine in {CONFIG.gcp.zone}...')
    print(f'Instance name:\t {instance_name}')
    print(f'Instance type:\t {machine_type}')
    print(f'Disk type:\t {CONFIG.gcp.disk_type}')


    key = (open('/home/runner/.ssh/id_rsa.pub')
          .read()
          .strip()
	  .translate(str.maketrans({'+': r'\+', ' ': r'\ '}))
    )

    github_job_name = (os.environ.get('GITHUB_JOB_FULL') or 'unknown').lower()

    print(LABELS)

    # Ensure compatibility with pre-67adc3a .vm_specs file.
    try:
        preemptible_machine = PREEMPT if CONFIG.machine.preemptible else 'false'
    except AttributeError:
        preemptible_machine = PREEMPT

    # Allow overriding the setting at workflow level.
    if preemptible_override is not None:
        preemptible_machine = PREEMPT if bool(preemptible_override) else 'false'

    print(f'Preemptible: {bool(preemptible_machine)}')

    # Create and start the virtual machine.
    gcloud_start = time.time()
    boot_disk_name = f"scalerunner-boot-disk"
    boot_disk_path = f"/dev/disk/by-id/scsi-0Google_PersistentDisk_{boot_disk_name}"
    boot_disk_ext_part = f"{boot_disk_path}-part2"

    external_disk_info = None
    # Attach an external disk (if applicable)
    external_disk_cmd = 'true'
    if disk_name:
        external_disk = get_gcp_disk(authed_session, CONFIG.gcp.project, CONFIG.gcp.zone, disk_name)
        if "name" not in external_disk or "sizeGb" not in external_disk:
            print("Unexpected output while processing response! Exiting!")
            sys.exit(1)
        print("Attaching external disk {} ({}GB)".format(external_disk['name'], external_disk['sizeGb']))
        external_disk_cmd = 'sudo mount /dev/disk/by-id/scsi-0Google_PersistentDisk_aux-part1 /mnt/aux'
        external_disk_info = {
                "autoDelete": "false",
                "deviceName": "aux",
                "mode": "READ_ONLY",
                "source": f"projects/{CONFIG.gcp.project}/zones/{CONFIG.gcp.zone}/disks/{external_disk['name']}"
            }
    create_instance(authed_session, instance_number, instance_name, key, boot_disk_name, external_disk_info, preemptible_machine, machine_type)
    print(f'Machine spawned in {elapsed(gcloud_start)} seconds.')

    target = os.environ[instance_name]
    ssh = create_ssh_connection(target)
    print('Machine ready')

    _, stdout, stderr = ssh.exec_command('sudo chown -R {0}:{0} /home/{0}'.format(USER))
    stdout_lines = stdout.readlines()
    stderr_lines = stderr.readlines()

    for l in stdout_lines:
        print(l.strip())

    for l in stderr_lines:
        print(l.strip())

    try:
        ssh_sftp = ssh.open_sftp()
        ssh_sftp.put(*PUBKEY)
        ssh_sftp.put(*SARGRAPH)
    except Exception as e:
        print('Copying initialization files failed!')
        print(e)
        sys.exit(1)

    container_sif_location = '/mnt/container.sif'
    node_sif_location = '/opt/sif/node.sif'
    node_zip_src = 'https://github.com/antmicro/github-actions-singularity-node/releases/download/node-16-v0.1.3/node-16-v0.1.3.zip'
    node_zip_dst = '/mnt/node.zip'
    infer_dns_cmd = "$(echo $SSH_CONNECTION | awk '{ print $1 }')"

    container_file = container_file if "/" in container_file else "library/" + container_file

    # The layout of the /mnt partition is as follows:
    #
    #   /mnt/1   -- overlay directory for $container
    #   /mnt/2   -- working directory (workspace)
    #   /mnt/3   -- overlay directory for Node container
    #   /mnt/aux -- mountpoint for auxiliary disk
    #

    commands = (
            'uname -a',
            'sudo mkdir -p /mnt/1 /mnt/2/work /mnt/aux /mnt/3',
            'sudo mkdir -p /etc/default',
            'sudo mkdir -p /opt/sif',
            r'echo "SYSLOGD_ARGS=\"-R {}:5140 -L\"" | sudo cp /dev/stdin /etc/default/syslogd'.format(infer_dns_cmd),
            'sudo /etc/init.d/S01syslogd restart',
            f'logger {LABELS}',
            external_disk_cmd,
            'echo "::group::Checking disks..."',
            'echo "------------------"',
            'sudo mount',
            'echo "------------------"',
            'sudo findmnt',
            'echo "------------------"',
            'sudo df -h',
            'echo "------------------"',
            'echo "::endgroup::"',
            f'sudo sh -c "test ! -f {node_sif_location} && curl -o {node_zip_dst} -Ls {node_zip_src} && unzip -p {node_zip_dst} node-16-alpine3.14.sif > {node_sif_location}"',
            f'sudo singularity pull --nohttps {container_sif_location} docker://{infer_dns_cmd}:5000/{container_file}',
            f'sudo singularity instance start -C -e --dns {infer_dns_cmd} --overlay /mnt/3 --bind /mnt/2:/root,/mnt/aux {node_sif_location} node',
            f'echo "::group::Starting {container_file}..."',
            f'sudo singularity --debug instance start -C -e --dns {infer_dns_cmd} --overlay /mnt/1 --bind /mnt/2:/root,/mnt/aux {container_sif_location} i',
            'echo "::endgroup::"',
            f'chmod +x {SARGRAPH[1]}',
            f'sudo mv {SARGRAPH[1]} /usr/bin/sargraph',
            f'cd /mnt && SARGRAPH_OUTPUT_TYPE=svg sudo -E sargraph chart start -f $(realpath {boot_disk_ext_part})',
            'echo "::group::Checking container disks..."',
            'sudo singularity exec -e instance://i df -h /',
            'echo "::endgroup::"',
    )

    execute_ssh_commands(ssh, commands)


def delete_vm(instance_number):
    instance_name = get_instance_name(instance_number)
    print("Attempting to delete a machine ({instance_name})..")
    credentials, _ = google.auth.default()
    authed_session = AuthorizedSession(credentials)
    delete_instance(authed_session, instance_name)
    print("Machine deleted ({instance_name})")


def check_preempted(current_log):
    for line in current_log:
        if "VM shutting down" in line:
            return line[line.find("VM shutting down"):]
    return None

def check_rsyslog(instance_number):
    instance_name = f'{get_instance_name(instance_number)}.c.{CONFIG.gcp.project}.internal'

    current_log = []
    found_labels = False

    for line in reversed(list(open(f"work/{instance_name}.log"))):
        if LABELS in line.rstrip():
            found_labels = True
            break
        current_log.append(line.rstrip())

    if not found_labels:
        print("Could not find rsyslog for current run!")
        sys.exit(1)
    else:
        status = check_preempted(current_log)
        if status is None:
            print("Could not get status of shutdown!")
            sys.exit(1)
        print(status)

def detect_preempted_signal(instance_number):
    instance_name = get_instance_name(instance_number)
    credentials, _ = google.auth.default()
    authed_session = AuthorizedSession(credentials)

    URL = f'https://compute.googleapis.com/compute/v1/projects/{CONFIG.gcp.project}/global/operations?filter="zone={CONFIG.gcp.zone} AND targetLink.basename()={instance_name} AND operationType=compute.instances.preempted"&orderBy=~startTime'
    r = authed_session.get(URL)
    operations_dict = json.loads(r)

    for operation in operations_dict:
        event_time = datetime.datetime.strptime(operation["startTime"], "%Y-%m-%dT%H:%M:%S.%f%z")
        now = datetime.datetime.utcnow().replace(tzinfo=datetime.timezone.utc)
        diff = now - event_time
        print(f"Found preempted event for instance: {instance_name} that occured: {diff} time ago")
        if diff.days == 0 and diff.seconds < 120:
            print("Found preempted event with diff lower than 2 minutes!")
            print(f"Instance {instance_name} killed by preempted event!")
            sys.exit(1)
    print(f"Couldn't find preempted event for instance: {instance_name}!")

def check_dmesg(instance_number):
    instance_name = get_instance_name(instance_number)
    target = os.environ[instance_name]
    ssh = create_ssh_connection(target)

    commands = (
            'sudo dmesg -T | grep -i "killed process"',
    )

    execute_ssh_commands(ssh, commands)

@click.command()
@click.option('--mode', type=click.Choice(['create_vm', 'delete_vm', 'check-rsyslog', 'detect_preempted_signal', 'check_dmesg']), required = True)
@click.option('-n', '--instance-number', help='Instance number', required=True)
@click.option('-s', '--container-file', help='Container file', required=False, default=None)
@click.option('-d', '--disk-name', help='External disk name', required=False, default=None)
@click.option('-p', '--preemptible-override', help='Override preemptible setting', required=False, type=int, default=None)
@click.option('-m', '--machine-type', help='Machine type to use', required=False, default=None)
def main(mode, instance_number, container_file=None, disk_name=None, preemptible_override=None, machine_type=None):
    if mode == "create_vm":
        if container_file is None or not container_file:
            print("Required 'container_file' parameter in create_vm missing or is empty!")
            sys.exit(1)
        create_vm(instance_number, container_file, disk_name, preemptible_override, machine_type)
    elif mode == "delete_vm":
        delete_vm(instance_number)
    elif mode == "check-rsyslog":
        check_rsyslog(instance_number)
    elif mode == "detect_preempted_signal":
        detect_preempted_signal(instance_number)
    elif mode == "check_dmesg":
        check_dmesg(instance_number)
    else:
        print(f"Unknown mode: {mode}! Exiting!")
        sys.exit(1)

if __name__ == '__main__':
    main()
