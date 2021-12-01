#!/usr/bin/python3
import os, sys, subprocess, json, click, paramiko, time, functools, platform, shlex, shutil
from collections import namedtuple

print = functools.partial(print, flush=True)

USER = 'scalerunner'
PUBKEY = os.path.join(os.path.expanduser('~'), '.ssh/id_rsa.pub'), f'/home/{USER}/.ssh/authorized_keys'
SARGRAPH = os.path.realpath('../sargraph/sargraph.py'), f'/home/{USER}/sargraph.py'
GCLOUD = shutil.which('gcloud')
PREEMPT = '--preemptible'
GH_ENV_LIST = ["GITHUB_JOB_FULL", "GITHUB_SHA", "GITHUB_RUN_ID"]

LABELS = ','.join(["{}={}".format(e.lower(), (os.environ.get(e) or 'null')[:63].lower()) for e in GH_ENV_LIST])

def load_config():
    with open('../.vm_specs.json', 'r') as f:
        return json.load(f, object_hook=lambda d: namedtuple('vm_specs', d.keys())(*d.values()))

CONFIG = load_config()

def elapsed(start):
    return round(time.time() - start, 2)

def get_gcp_disk(disk_name, zone):
    if not disk_name or not zone:
        return None

    cmd = "{} compute disks describe {} --zone={} --format=json".format(
            GCLOUD, disk_name, zone)

    return json.loads(
            subprocess.check_output(
                shlex.split(cmd),
                stderr=subprocess.DEVNULL,
                timeout=10
                )
            )

def export_runner_ip_addr(create_instance_output, runner_name, preemptible):
    ip_index = 4 if preemptible else 3

    for line in create_instance_output.splitlines():
        splitted_line = line.split()
        if runner_name in splitted_line[0]:
            if len(splitted_line) > ip_index:
                ip = splitted_line[ip_index]
                # Environment variables doesn't get exported
                # to the parent process, this export is only for
                # current script, later runner parses below output
                # and sets correct ip in the parent process
                os.environ[runner_name] = ip
                print(f"export {runner_name}={ip}")
            else:
                print("Couldn't find runner ip address! Exiting!")
                os.exit(1)

def create_vm(instance_number, container_file, disk_name=None, preemptible_override=None):
    instance_name = f'{platform.node()}-auto-spawned{instance_number}'
    try:
        external_disk = get_gcp_disk(
                disk_name=disk_name,
                zone=CONFIG.gcp.zone,
                )
    except subprocess.CalledProcessError:
        print('Unable to access requested external disk!')
        sys.exit(1)

    coordinator_type_cmd = 'gcloud compute instances describe ' \
                           '$(hostname) ' \
                           f'--zone {CONFIG.gcp.zone} ' \
                           '--format=\'table(machineType)\''

    try:
        coordinator_type = subprocess.check_output(
                coordinator_type_cmd,
                shell=True,
                stderr=subprocess.STDOUT,
        ).decode("utf-8")

        coordinator_type = coordinator_type[coordinator_type.rfind("/") + 1:].replace(CONFIG.gcp.project, '***')
        print(f'Using coordinator machine: {coordinator_type}')

    except subprocess.CalledProcessError as err:
        print('Failed to get coordinator machine type!')
        print('\n'+coordinator_type.output.decode().replace(CONFIG.gcp.project, '***'))
        sys.exit(1)

    print(f'Spawning a GCP machine in {CONFIG.gcp.zone}...')
    print(f'Instance name:\t {instance_name}')
    print(f'Instance type:\t {CONFIG.gcp.type}')
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
        preemptible_machine = PREEMPT if CONFIG.machine.preemptible else ''
    except AttributeError:
        preemptible_machine = PREEMPT

    # Allow overriding the setting at workflow level.
    if preemptible_override is not None:
        preemptible_machine = PREEMPT if bool(preemptible_override) else ''

    print(f'Preemptible: {bool(preemptible_machine)}')

    # Create and start the virtual machine.
    gcloud_start = time.time()

    instance_cmd = 'gcloud beta compute --verbosity=error ' \
            f'--project={CONFIG.gcp.project} ' \
            f'instances create {instance_name} --zone={CONFIG.gcp.zone} ' \
            f'--machine-type={CONFIG.gcp.type} --subnet={CONFIG.gcp.subnet} ' \
            '--no-address --network-tier=PREMIUM ' \
            '--metadata=serial-port-enable=true,' \
            'ssh-keys=coordinator:' \
            f'{key} ' \
            f'--labels=' \
            f'{LABELS} ' \
            '--no-restart-on-failure --tags=runners ' \
            '--maintenance-policy=TERMINATE ' \
            f'{preemptible_machine} ' \
            '--no-service-account ' \
            '--no-scopes ' \
            f'--image={CONFIG.gcp.image} --image-project={CONFIG.gcp.project} ' \
            f'--boot-disk-size={CONFIG.machine.disk}GB ' \
            f'--boot-disk-type={CONFIG.gcp.disk_type} ' \
            f'--boot-disk-device-name={instance_name} ' \
            '--reservation-affinity=any'

    try:
        output = subprocess.check_output(
                instance_cmd,
                shell=True,
                stderr=subprocess.STDOUT,
        ).decode("utf-8")

        output = output.replace(CONFIG.gcp.project, '***')

        print('\n'+output)

        export_runner_ip_addr(output, instance_name, preemptible_machine is PREEMPT)

    except subprocess.CalledProcessError as err:
        print('\n'+err.output.decode().replace(CONFIG.gcp.project, '***'))
        sys.exit(1)

    print(f'Machine spawned in {elapsed(gcloud_start)} seconds.')

    # Attach an external disk (if applicable)
    external_disk_cmd = 'true'

    if external_disk is not None:
        print("Attaching external disk {} ({}GB)".format(external_disk['name'], external_disk['sizeGb']))

        attach_disk = "gcloud compute instances attach-disk {} --disk={} --device-name=aux --zone={} --mode=ro".format(
                instance_name, external_disk['name'], CONFIG.gcp.zone
                )

        external_disk_cmd = 'sudo mount /dev/disk/by-id/scsi-0Google_PersistentDisk_aux-part1 /mnt/aux'

        try:
            output = subprocess.check_output(
                    attach_disk,
                    shell=True,
                    stderr=subprocess.STDOUT,
            ).decode("utf-8")

            print('\n'+output.replace(CONFIG.gcp.project, '***'))
        except subprocess.CalledProcessError as err:
            print('\n'+err.output.decode().replace(CONFIG.gcp.project, '***'))
            sys.exit(1)
    
    # this is the name recognized by DNS in Google
    target = os.environ[instance_name]

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
            print('Machine ready')

            _, stdout, stderr = ssh.exec_command('sudo chown -R {0}:{0} /home/{0}'.format(USER))
            stdout_lines = stdout.readlines()
            stderr_lines = stderr.readlines()

            for l in stdout_lines:
                print(l.strip())

            for l in stderr_lines:
                print(l.strip())

            break
        except Exception as e:
            if ssh_timeout_c % 10 == 0:
                print('Waiting for SSH... [{}/{}] '.format((int)((ssh_timeout - ssh_timeout_c) / 10), (int)(ssh_timeout / 10)))
            ssh_timeout_c -= 1

            if ssh_timeout_c == 0:
                print('Timeout while waiting for SSH!')
                print(e)
                sys.exit(1)

            time.sleep(1)

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
            'echo "Checking disks ..."',
            'echo "------------------"',
            'sudo mount',
            'echo "------------------"',
            'sudo findmnt',
            'echo "------------------"',
            'sudo df -h',
            'echo "------------------"',
            f'sudo sh -c "test ! -f {node_sif_location} && curl -o {node_zip_dst} -Ls {node_zip_src} && unzip -p {node_zip_dst} node-16-alpine3.14.sif > {node_sif_location}"',
            f'sudo singularity pull --nohttps {container_sif_location} docker://{infer_dns_cmd}:5000/{container_file}',
            f'sudo singularity instance start -C -e --dns {infer_dns_cmd} --overlay /mnt/3 --bind /mnt/2:/root,/mnt/aux {node_sif_location} node',
            f'echo "Starting {container_file}..."',
            f'sudo singularity --debug instance start -C -e --dns {infer_dns_cmd} --overlay /mnt/1 --bind /mnt/2:/root,/mnt/aux {container_sif_location} i',
            f'chmod +x {SARGRAPH[1]}',
            f'sudo mv {SARGRAPH[1]} /usr/bin/sargraph',
            'cd /mnt && SARGRAPH_OUTPUT_TYPE=svg sudo -E sargraph chart start',
            'sudo singularity exec -e instance://i df -h /',
    )

    for cmd in commands:
        _, stdout, stderr = ssh.exec_command(cmd)

        stdout_lines = stdout.readlines()
        stderr_lines = stderr.readlines()

        for l in stdout_lines:
            print(l.strip())

        for l in stderr_lines:
            print(l.strip())

def check_preempted(current_log):
    for line in current_log:
        if "VM shutting down" in line:
            return line[line.find("VM shutting down"):]
    return None

def check_rsyslog(instance_number):
    instance_name = f'{platform.node()}-auto-spawned{instance_number}.c.{CONFIG.gcp.project}.internal'

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
            os.exit(1)
        print(status)

def detect_preempted_signal(instance_number):
    instance_name = f'{platform.node()}-auto-spawned{instance_number}'

    operation_list_cmd = f'gcloud compute operations list ' \
                         f'--project={CONFIG.gcp.project} ' \
                         f'--filter="zone={CONFIG.gcp.zone} AND targetLink.basename()={instance_name} AND operationType=compute.instances.preempted" ' \
                         f'--format=json ' \
                         f'--sort-by=~startTime'
    try:
        operation_list = subprocess.check_output(
                operation_list_cmd,
                shell=True,
                stderr=subprocess.STDOUT,
        ).decode("utf-8")
        json_output = ""
        for line in operation_list.splitlines():
            # gcloud returns WARNING message, if there isn't any operations
            # matching filter, we need to skip this warning to get parsable json
            if not line.startswith("WARNING:"):
                json_output += line

        operations_dict = json.loads(json_output)

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
    except subprocess.CalledProcessError as err:
        print('Failed to get operation list!')
        print('\n'+operation_list_cmd.replace(CONFIG.gcp.project, '***'))
        sys.exit(1)

@click.command()
@click.option('--mode', type=click.Choice(['create_vm', 'check-rsyslog', 'detect_preempted_signal']), required = True)
@click.option('-n', '--instance-number', help='Instance number', required=True)
@click.option('-s', '--container-file', help='Container file', required=False, default=None)
@click.option('-d', '--disk-name', help='External disk name', required=False, default=None)
@click.option('-p', '--preemptible-override', help='Override preemptible setting', required=False, type=int, default=None)
def main(mode, instance_number, container_file=None, disk_name=None, preemptible_override=None):
    if mode == "create_vm":
        if container_file is None or not container_file:
            print("Required 'container_file' parameter in create_vm missing or is empty!")
            os.exit(1)
        create_vm(instance_number, container_file, disk_name, preemptible_override)
    elif mode == "check-rsyslog":
        check_rsyslog(instance_number)
    elif mode == "detect_preempted_signal":
        detect_preempted_signal(instance_number)
    else:
        print(f"Unknown mode: {mode}! Exiting!")
        os.exit(1)

if __name__ == '__main__':
    main()
