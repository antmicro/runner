#!/usr/bin/python3
import os, sys, subprocess, json, click, paramiko, time, functools, platform, shlex, shutil, requests, uuid, datetime, random, socket, warnings, logging, logging.handlers
from collections import namedtuple, OrderedDict
from cryptography.utils import CryptographyDeprecationWarning

warnings.filterwarnings("ignore", category=CryptographyDeprecationWarning)

print = functools.partial(print, flush=True)

libs = ['google-auth-library-python', 'cachetools/src']

for l in libs:
    sys.path.insert(0, os.path.dirname(os.path.realpath(__file__))+f'/../extra_python_deps/{l}')

import google.auth
from google.auth.transport.requests import AuthorizedSession

# Configure syslog-backed logging for Paramiko.
logging.raiseExceptions = False

paramiko_syslog_handler = logging.handlers.SysLogHandler(
        address="/tmp/gha_paramiko_log.sock",
        facility=logging.handlers.SysLogHandler.LOG_LOCAL0
        )
paramiko_syslog_handler.setFormatter(logging.Formatter("[%(process)s] %(levelname)s:%(name)s:%(message)s"))

paramiko_logger = logging.getLogger("paramiko")
paramiko_logger.setLevel(logging.DEBUG)
paramiko_logger.addHandler(paramiko_syslog_handler)

USER = 'scalerunner'
PUBKEY = os.path.join(os.path.expanduser('~'), '.ssh/id_rsa.pub'), f'/home/{USER}/.ssh/authorized_keys'
SARGRAPH = os.path.realpath('../sargraph/sargraph.py'), f'/home/{USER}/sargraph.py'
PREEMPT = 'true'
GH_ENV_LIST = ["GITHUB_JOB_FULL", "GITHUB_SHA", "GITHUB_RUN_ID"]

SARGRAPH_RAMDISK_SIZE_MB = 50

LABELS = [{e.lower(): (os.environ.get(e) or 'null')[:63].lower() for e in GH_ENV_LIST}]

GCP_RESOURCE_EXHAUSTION_ERR = 'ZONE_RESOURCE_POOL_EXHAUSTED'
GCP_RESOURCE_EXHAUSTION_EXAMPLE_ERR = [
        {
            'code': '{}_WITH_DETAILS'.format(GCP_RESOURCE_EXHAUSTION_ERR), 
            'message': "The zone 'projects/foo/zones/bar' does not have enough resources available to fulfill the request.  '(resource type:compute)'."
            }
        ]

SIMULATE_EXHAUSTION_CTR = 0

def load_config():
    with open('../.vm_specs.json', 'r') as f:
        return json.load(f, object_hook=lambda d: namedtuple('vm_specs', d.keys())(*d.values()))

CONFIG = load_config()

def str2bool(v):
    return v.lower() in ("yes", "true", "t", "1")

def get_project_id(numeric=False):
    prop = "numeric-project-id" if numeric else "project-id"
    with requests.get(f"http://metadata.google.internal/computeMetadata/v1/project/{prop}", headers={'Metadata-Flavor':'Google'}) as r:
        r.raise_for_status()
        return r.text

PROJECT, PROJECT_ID = get_project_id(), get_project_id(True)
AUTHED_SESSION = AuthorizedSession(google.auth.default()[0])

def get_current_network():
    with requests.get(f"http://metadata.google.internal/computeMetadata/v1/instance/network-interfaces/0/network", headers={'Metadata-Flavor':'Google'}) as r:
        r.raise_for_status()
        return r.text.split('/')[-1]

def get_available_subnetworks(network_name=None):
    network_name = network_name or get_current_network()

    with AUTHED_SESSION.get(f"https://compute.googleapis.com/compute/v1/projects/{PROJECT}/aggregated/subnetworks", params={'filter': f'(network eq .*\\b{network_name}\\b.*)'}) as r:
        r.raise_for_status()
        return r.json()['items']

def get_available_zones():
    current_network = get_current_network()

    # Get home zone.
    with requests.get(f"http://metadata.google.internal/computeMetadata/v1/instance/zone", headers={'Metadata-Flavor':'Google'}) as r:
        r.raise_for_status()
        home_zone = r.text.split('/')[-1]

    # Get current network metadata to retrieve the list of subnetworks and extract their regions.
    regions_with_subnets = {region_key.split('/')[-1]:region_val for region_key, region_val in get_available_subnetworks(current_network).items() if "subnetworks" in region_val}

    # Get all available regions in order to get their zones.
    with AUTHED_SESSION.get(f"https://compute.googleapis.com/compute/v1/projects/{PROJECT}/regions") as r:
        r.raise_for_status()
        regions = r.json()['items']

    zones = []
    aux_zones = []

    # Extract zones belonging to all regions with subnetworks associated with the current network.
    for region in regions:
        for zone in region['zones']:
            if any(region_with_subnet in zone for region_with_subnet in regions_with_subnets.keys()):
                split_zone = zone.split('/')[-1]

                to_append = (
                        split_zone,
                        next(filter(lambda s: s['name'].startswith(current_network), regions_with_subnets[split_zone[:-2]]['subnetworks']))
                        )

                if split_zone == home_zone:
                    zones.insert(0, to_append)
                elif split_zone.startswith(home_zone[:-2]):
                    zones.append(to_append)
                else:
                    aux_zones.append(to_append)

    return OrderedDict(zones+aux_zones)

def get_secret(secret_name, namespace):
    base_url = f"https://secretmanager.googleapis.com/v1/projects/{PROJECT_ID}/secrets/{secret_name}"

    with AUTHED_SESSION.get(base_url) as r:
        r.raise_for_status()
        labels = r.json().get('labels') or dict()

        if not str2bool(labels.get('gha_runner_exposed') or ''):
            print("Requested secret has not been made available to GHA runners.")
            sys.exit(1)

        if labels.get("gha_runner_namespace") != namespace:
            print("Requested secret does not belong to the current namespace.")
            sys.exit(1)

    # TODO: wrap in try-catch/check error.
    with AUTHED_SESSION.get(f"{base_url}/versions", params={'filter':'state:ENABLED'}) as r:
        r.raise_for_status()
        versions = r.json()['versions']

        if len(versions) > 1:
            print('Requested secret has more than one enabled versions.')
            sys.exit(1)

        active_version = int(versions[0]['name'].split('/')[-1])

    with AUTHED_SESSION.get(f"{base_url}/versions/{active_version}:access") as r:
        r.raise_for_status()
        secret = r.json()['payload']['data']

    print(secret)

def wait_for_gcp(link):
    for _ in range(0, 100):
        r = AUTHED_SESSION.get(link)

        print("Operation resource: {}, status code: {}, content: {}".format(link, r.status_code, r.text), file=sys.stderr)

        try:
            result = r.json()
        except requests.exceptions.JSONDecodeError:
            return [{'e': "Unable to parse JSON while waiting for GCP operation"}]

        operation_status = result.get('status')

        if operation_status is None:
            return [{'e': "Status field is not present in the response"}]

        # The status of the operation can be one of the following: PENDING, RUNNING, or DONE.
        if result['status'] == 'DONE':
            if 'error' in result:
                if 'errors' in result['error']:
                    return result['error']['errors']
                elif 'reason' in result['error']:
                    if result['error']['reason'] == "RATE_LIMIT_EXCEEDED":
                        print(f"Quota exceeded for API calls, will try again in one minute...") 
                        time.sleep(60)
                        continue
            else:
                return result
        else:
            time.sleep(1)
            continue


def export_gcp_ip(link):
    with AUTHED_SESSION.get(link) as r:
        r.raise_for_status()
        result = r.json()

    ip = result['networkInterfaces'][0]['networkIP']
    runner_name = result['name']

    print("export {}={}".format(result['name'], ip))
    return ip

def list_instances(instance_name=None):
    params = {}

    if instance_name is not None:
        params['filter'] = f'(name eq .*\\b{instance_name}\\b.*)'

    with AUTHED_SESSION.get(f"https://compute.googleapis.com/compute/v1/projects/{PROJECT}/aggregated/instances", params=params) as r:
        r.raise_for_status()
        zones_with_instances = r.json()['items']

        for zone_name, zone_object in zones_with_instances.items():
            for instance in (zone_object.get("instances") or []):
                yield instance

def describe_instance(instance_name):
    for instance in list_instances(instance_name):
        if instance['name'] == instance_name:
            return instance

def create_instance_call(instance_number, instance_name, boot_disk_name, external_disk_info, preemptible_machine, machine_type, service_account, zone, subnetwork, uuid):
    URL = f"https://compute.googleapis.com/compute/v1/projects/{PROJECT_ID}/zones/{zone}/instances?requestId={uuid}"
    data = {
        "name": f"{instance_name}",
        "machineType": f"zones/{zone}/machineTypes/{machine_type}",
        "networkInterfaces": [{
            "subnetwork": subnetwork,
        }],
        "metadata": {
            "items": [
                {
                    "key": "serial-port-enable",
                    "value": "true",
                },
                {
                    "key": "ssh-keys",
                    "value": f"{USER}:{open('/home/runner/.ssh/id_rsa.pub').read().strip()}",
                },
            ],
        },
        "scheduling": {
            "automaticRestart": "false",
            "onHostMaintenance": "MIGRATE",
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
                "sourceImage": f"projects/{PROJECT_ID}/global/images/{CONFIG.gcp.image}",
            },
        },
            external_disk_info
        ],
        "reservationAffinity": {
            "consumeReservationType": "any"
        },
        "labels": LABELS,
        "serviceAccounts": service_account,
    }
    return AUTHED_SESSION.post(url=URL, json=data)

def create_instance(instance_number, instance_name, boot_disk_name, external_disk_info, preemptible_machine, machine_type, service_account, zone, subnetwork):
    # Same UUID makes sure that we won't create multiple VMs
    request_uuid = str(uuid.uuid4())

    global SIMULATE_EXHAUSTION_CTR

    simulate_exhaustion = os.environ.get('SIMULATE_EXHAUSTION')

    if simulate_exhaustion is not None:
        if SIMULATE_EXHAUSTION_CTR != int(simulate_exhaustion):
            SIMULATE_EXHAUSTION_CTR+=1
            return GCP_RESOURCE_EXHAUSTION_EXAMPLE_ERR

    # Make a request to create the instance.
    r = create_instance_call(
            instance_number, 
            instance_name, 
            boot_disk_name, 
            external_disk_info, 
            preemptible_machine, 
            machine_type, 
            service_account,
            zone,
            subnetwork,
            request_uuid
            )
    r.raise_for_status()

    result = r.json()

    return wait_for_gcp(result['selfLink'])

def elapsed(start):
    return round(time.time() - start, 2)

def get_gcp_disks(disk_name):
    url = f"https://compute.googleapis.com/compute/v1/projects/{PROJECT}/aggregated/disks"
    disks = {}

    # First obtain the main disk.
    r1 = AUTHED_SESSION.get(url, params={'filter': f'(name eq {disk_name})'})
    r1.raise_for_status()

    # Next obtain disk designated as replicas (by assigning the 'gha-replica-for' label).
    r2 = AUTHED_SESSION.get(url, params={'filter': f'(labels.gha-replica-for:{disk_name})'})
    r2.raise_for_status()

    for r in [r1, r2]:
        for zone_name, zone_object in r.json()['items'].items():
            for disk in (zone_object.get("disks") or []):
                # Attempt to find GCP-level regional replicas (e.g. when disk type is regional balanced persistent disk).
                for nested_zone_name in (disk.get('replicaZones') or [zone_name]):
                    disks[nested_zone_name.split('/')[-1]] = {
                            "autoDelete": "false",
                            "deviceName": "aux",
                            "mode": "READ_ONLY",
                            "source": relative_self_link(disk['selfLink']) 
                            }
    return disks

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

def create_ssh_connection(target, verbose=True):
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.client.AutoAddPolicy())

    ssh_timeout = ssh_timeout_c = 50

    while ssh_timeout_c > 0:
        # Print every tenth occurence.
        if verbose and ssh_timeout_c % 10 == 0:
            print('Waiting for SSH... [{}/{}] '.format(
                int(((ssh_timeout - ssh_timeout_c) / 10) + 1),
                int(ssh_timeout / 10))
                )

        # Timeout exceeded.
        if ssh_timeout_c == 0:
            print('Timeout while waiting for SSH!')
            print(e)
            sys.exit(1)

        # Attempt to connect.
        try:
            ssh.connect(
                    target,
                    username=USER,
                    timeout=1,
                    auth_timeout=1,
                    banner_timeout=1,
            )
        except paramiko.ssh_exception.AuthenticationException:
            # Pre-62142bfbecb765d9838782904c735eb83e9743b8 images don't add public keys from VM metadata.
            # We used to rely on using password authentication during this step.
            # To make the public key authentication work later on, coordinator's key was SCPed at the end.
            #
            # In order to ensure backward compatibility, we detect if password auth is available (it isn't on newer images)
            # and if so, we SCP the key to the node and repeat the loop.
            t = paramiko.Transport((target, paramiko.config.SSH_PORT))

            try:
                t.connect()
                t.auth_none('')
            except paramiko.ssh_exception.BadAuthenticationType as e:
                if "password" in e.allowed_types:
                    if verbose:
                        print("Falling back to the old initial authentication method...")
                    try:
                        ssh.connect(
                                target,
                                username=USER,
                                password=USER
                                )
                        ssh_sftp = ssh.open_sftp()
                        ssh_sftp.put(*PUBKEY)
                        ssh_sftp.close()
                        ssh.close()
                    except Exception as e:
                        print("Public key authentication failed and password auth is not available!")
                        sys.exit(1)
            except paramiko.ssh_exception.SSHException as e:
                print("Error occured while detecting authentication methods!")
                sys.exit(1)
            finally:
                t.close()
        except (socket.timeout, paramiko.ssh_exception.NoValidConnectionsError) as e:
            pass
        finally:
            # Progress timeout count if connection has not been established.
            if ssh.get_transport() is None or (ssh.get_transport() is not None and not ssh.get_transport().is_active()):
                ssh_timeout_c -= 1
                time.sleep(1)
            else:
                break

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

        while not stdout.channel.exit_status_ready():
            print(f"Exit code for command <{cmd}> is not ready yet", file=sys.stderr)
            time.sleep(1)

        if stdout.channel.recv_exit_status()!= 0:
            print(f"Error while setting up machine! Exiting!")
            sys.exit(1)

def get_instance_name(instance_number):
    return f'{platform.node()}-auto-spawned{instance_number}'

def delete_instance_call(instance_name, uuid):
    instance_zone = describe_instance(instance_name)['zone'].split('/')[-1]

    URL = f"https://compute.googleapis.com/compute/v1/projects/{PROJECT_ID}/zones/{instance_zone}/instances/{instance_name}?requestID={uuid}"
    r = AUTHED_SESSION.delete(URL)
    return json.loads(r.text)

def delete_instance(instance_name):
    success = False
    retries = 5
    request_uuid = str(uuid.uuid4())
    while retries > 0:
        retries = retries - 1
        # Same UUID makes sure that we won't delete multiple VMs
        result = delete_instance_call(instance_name, request_uuid)
        if "selfLink" not in result:
            print("Unexpected output while processing response!")
            continue
        wait_for_gcp(result['selfLink'])
        success = True
        break
    if success is False:
        print(f"Couldn't delete instance: {instance_name}! Exiting!")
        sys.exit(1)

def relative_self_link(self_link):
    return self_link.replace("https://www.googleapis.com/compute/v1/", "")

def create_vm(instance_number, container_file, disk_name=None, preemptible_override=None, machine_type=None, service_account=None, ssh_tunnel_config=None, ssh_tunnel_key=None):
    print("Attempting to spawn a machine... (PID: {})".format(os.getpid()))

    machine_type = machine_type or CONFIG.gcp.type
    check_machine_type(machine_type)

    instance_name = get_instance_name(instance_number)

    print(f'Instance name:\t {instance_name}')
    print(f'Instance type:\t {machine_type}')
    print(f'Disk type:\t {CONFIG.gcp.disk_type}')


    github_job_name = (os.environ.get('GITHUB_JOB_FULL') or 'unknown').lower()

    print(f"LABELS: {str(LABELS)}")

    # Ensure compatibility with pre-67adc3a .vm_specs file.
    try:
        preemptible_machine = PREEMPT if CONFIG.machine.preemptible else 'false'
    except AttributeError:
        preemptible_machine = PREEMPT

    # Allow overriding the setting at workflow level.
    if preemptible_override is not None:
        preemptible_machine = PREEMPT if bool(preemptible_override) else 'false'

    print(f'Preemptible: {str2bool(preemptible_machine)}')

    # First element is guaranteed to be the home zone (i.e. coordinator machine zone).
    zones_and_subnets = get_available_zones()
    available_zones = list(zones_and_subnets.keys())

    # Create and start the virtual machine.
    gcloud_start = time.time()
    boot_disk_name = f"scalerunner-boot-disk"
    boot_disk_path = f"/dev/disk/by-id/scsi-0Google_PersistentDisk_{boot_disk_name}"
    boot_disk_ext_part = f"{boot_disk_path}-part2"

    ssh_tunnel_cmd = 'true'

    if ssh_tunnel_config and ssh_tunnel_key:
        ssh_tunnel_cmd = f'sudo singularity run --app sshtunnel instance://util 1 "{ssh_tunnel_config}" "{ssh_tunnel_key}"'

    service_account_info = None
    external_disk_cmd = 'true'

    # Attach an external disk (if applicable)
    if disk_name:
        # Obtain the main disk and zonal replicas.
        available_external_disks = get_gcp_disks(disk_name)

        # Bail if no disk and/or zonal replicas were found.
        if len(available_external_disks) == 0:
            print(f"Disk named {disk_name} was not found!")
            sys.exit(1)
        
        print(f"Requested external disk was found in {len(available_external_disks)} zone(s)")

        external_disk_cmd = 'sudo mount /dev/disk/by-id/scsi-0Google_PersistentDisk_aux-part1 /mnt/aux'

    if service_account:
        if "gh-sa-" not in service_account:
            print("Used service account must have 'gh-sa-' in the name!")
            sys.exit(1)
        service_account_info = [{
                "email": f"{service_account}@{PROJECT}.iam.gserviceaccount.com",
                "scopes": [
                    "https://www.googleapis.com/auth/cloud-platform", # this scope is required in order to get authentication details from the Google Compute Engine metadata service
                    "https://www.googleapis.com/auth/devstorage.read_write"
                ]
            }]

    successful_creation = False

    for zone, subnet in zones_and_subnets.items():
        print(f'Attempting to spawn a machine in {zone}')

        external_disk_info = None

        if disk_name:
            try:
                external_disk_info = available_external_disks[zone]
            except KeyError:
                print(f'External disk or its replica is not available in {zone}, skipping it...')
                continue

        try:
            create_result = create_instance(
                    instance_number, 
                    instance_name, 
                    boot_disk_name, 
                    external_disk_info, 
                    preemptible_machine, 
                    machine_type, 
                    service_account_info,
                    zone,
                    subnet['selfLink'],
                    )
        except requests.exceptions.HTTPError as e:
            print(e.response.text)
            sys.exit(1)

        if isinstance(create_result, dict):
            print(f'Machine spawned in {elapsed(gcloud_start)} seconds.')
            successful_creation = True
            break
        elif isinstance(create_result, list):
            for create_error in create_result:
                if (create_error.get('code') or '').startswith(GCP_RESOURCE_EXHAUSTION_ERR):
                    print(f'{GCP_RESOURCE_EXHAUSTION_ERR} in {zone}, will try another one...')
                    continue
                else:
                    print(f'Error occured while spawning the instance: {create_error}')
                    sys.exit(1)

    if not successful_creation:
        print('No defined zone is able to serve the request at the moment.')
        sys.exit(1)

    target = export_gcp_ip(create_result['targetLink']) 
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
        ssh_sftp.put(*SARGRAPH)
    except Exception as e:
        print('Copying initialization files failed!')
        print(e)
        sys.exit(1)
    finally:
        ssh_sftp.close()

    container_sif_location = '/mnt/container.sif'

    node_sif_location = '/opt/sif/node.sif'
    node_zip_src = 'https://github.com/antmicro/github-actions-singularity-node/releases/download/node-16-v0.1.3/node-16-v0.1.3.zip'
    node_zip_dst = '/mnt/node.zip'

    util_sif_location = '/opt/sif/util.sif'
    util_zip_src = 'https://github.com/antmicro/github-actions-singularity-utility-container/releases/download/v0.1.2/gha-utility-container-v0.1.2.zip'
    util_zip_dst = '/mnt/util.zip'

    infer_dns_cmd = "$(echo $SSH_CONNECTION | awk '{ print $1 }')"

    # Truncate local rsyslog log file
    log_name = f'work/{get_instance_name(instance_number)}.c.{PROJECT}.internal.log'
    command = f"{shutil.which('truncate')} -s0 {log_name}"
    try:
        subprocess.run(shlex.split(command),stderr=subprocess.STDOUT)
    except subprocess.CalledProcessError as err:
        print("Error while truncating local rsyslog file!")
        print(err)
        # Don't error out here, as it is not critical task

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
            'sudo mkdir -p /mnt/1 /mnt/2/work /mnt/aux /mnt/3 /mnt/sargraph-mount /mnt/ram-disk',
            'sudo mkdir -p /etc/default',
            'sudo mkdir -p /opt/sif',
            r'echo "SYSLOGD_ARGS=\"-R {}:5140 -L\"" | sudo cp /dev/stdin /etc/default/syslogd'.format(infer_dns_cmd),
            'sudo /etc/init.d/S01syslogd restart',
            f'logger {str(LABELS)}',
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
            'echo "::group::Starting sargraph.."',
            f'sudo mount -t tmpfs -o size={SARGRAPH_RAMDISK_SIZE_MB}m tmpfs /mnt/ram-disk 2>&1 > /dev/null',
            f'sudo dd if=/dev/zero of=/mnt/ram-disk/sargraph-disk bs=1M count={SARGRAPH_RAMDISK_SIZE_MB} 2>&1 > /dev/null',
            'sudo mke2fs -F /mnt/ram-disk/sargraph-disk 2>&1 > /dev/null',
            'sudo mount /mnt/ram-disk/sargraph-disk /mnt/sargraph-mount',
            f'chmod +x {SARGRAPH[1]}',
            f'sudo mv {SARGRAPH[1]} /usr/bin/sargraph',
            f'cd /mnt/sargraph-mount && SARGRAPH_OUTPUT_TYPE=svg sudo -E sargraph chart start -f $(realpath {boot_disk_ext_part})',
            'echo "::endgroup::"',
            f'sudo sh -c "test ! -f {node_sif_location} && curl -o {node_zip_dst} -Ls {node_zip_src} && unzip -p {node_zip_dst} node-16-alpine3.14.sif > {node_sif_location}" || true',
            f'sudo sh -c "test ! -f {util_sif_location} && curl -o {util_zip_dst} -Ls {util_zip_src} && unzip -p {util_zip_dst} image.sif > {util_sif_location}" || true',
            f'sudo singularity pull --nohttps {container_sif_location} docker://{infer_dns_cmd}:5000/{container_file}',
            f'sudo singularity instance start -C -e --dns {infer_dns_cmd} --overlay /mnt/3 --bind /mnt/2:/root,/mnt/aux,/mnt/sargraph-mount {node_sif_location} node',
            f'sudo singularity instance start -C -e --dns {infer_dns_cmd} --writable-tmpfs {util_sif_location} util',
            ssh_tunnel_cmd,
            f'echo "::group::Starting {container_file}..."',
            f'sudo singularity --debug instance start -C -e --dns {infer_dns_cmd} --overlay /mnt/1 --bind /mnt/2:/root,/mnt/aux {container_sif_location} i',
            'echo "::endgroup::"',
            'echo "::group::Checking container disks..."',
            'sudo singularity exec -e instance://i df -h /',
            'echo "::endgroup::"',
    )

    execute_ssh_commands(ssh, commands)


def delete_vm(instance_number):
    instance_name = get_instance_name(instance_number)
    print(f"Attempting to delete a machine ({instance_name})..")
    delete_instance(instance_name)
    print(f"Machine deleted ({instance_name})")


def check_preempted(current_log):
    for line in current_log:
        if "VM shutting down" in line:
            return line[line.find("VM shutting down"):]
    return None

def check_rsyslog(instance_number):
    instance_name = f'{get_instance_name(instance_number)}.c.{PROJECT}.internal'

    current_log = []
    found_labels = False

    for line in reversed(list(open(f"work/{instance_name}.log"))):
        if str(LABELS).replace("\'", "") in line.rstrip():
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

    URL = f'https://compute.googleapis.com/compute/v1/projects/{PROJECT_ID}/aggregated/operations'

    r = AUTHED_SESSION.get(URL, params={'filter': f'(operationType eq compute.instances.preempted)', 'maxResults': 500})

    if not str(r.status_code).startswith("2"):
        print("Unable to get the list of operations!")
        print(r.text)
        sys.exit(1)

    items = json.loads(r.text).get('items')

    if items is not None:
        for zone, items_in_zone in items.items():
            for operation in (items_in_zone.get('operations') or []):
                if operation['targetLink'].split('/')[-1] == instance_name:
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
    ssh = create_ssh_connection(target, verbose=False)

    commands = (
            'sudo dmesg -T | grep -i "killed process" || true',
    )

    execute_ssh_commands(ssh, commands)

def list_auto_spawned_instances():
    return list(list_instances(get_instance_name('.*')))

def delete_stale_instances():
    now = datetime.datetime.utcnow().replace(tzinfo=datetime.timezone.utc)
    for instance in list_auto_spawned_instances():
        if "creationTimestamp" not in instance or "name" not in instance:
            print("Unexpected response while listing instances! Exiting!")
            sys.exit(1)
        print(f"Checking {instance['name']}..")
        event_time = datetime.datetime.strptime(instance["creationTimestamp"], "%Y-%m-%dT%H:%M:%S.%f%z")
        diff = now - event_time
        hours = diff.seconds/60/60
        # self hosted runners can run jobs for more then 6 hours:
        # https://github.community/t/is-that-possible-to-run-job-which-takes-more-than-6-hours-on-self-hosted-runner/17121/8
        # assume maximum timeout 12h
        if diff.days > 0 or hours > 12:
            print(f"Attempting to delete a stale machine({instance['name']}) spawned {instance['creationTimestamp']}..")
            delete_instance(instance['name'])

def check_mode_parameters(mode, instance_number, container_file):
    if mode == "create_vm":
        if container_file is None or not container_file:
            print(f"Required 'container_file' parameter in {mode} is missing or is empty!")
            sys.exit(1)
    if mode in ["create_vm", "delete_vm", "check-rsyslog", "detect_preempted_signal", "check_dmesg"]:
        if instance_number is None:
            print(f"Required 'instance-number' parameter in {mode} is missing!")
            sys.exit(1)

@click.command()
@click.option('--mode', type=click.Choice(['create_vm', 'delete_vm', 'check-rsyslog', 'detect_preempted_signal', 'check_dmesg', 'delete_stale_instances', 'get_secret', 'get_project_id', 'get_zones', 'get_vm', 'get_vms', 'get_disks']), required = True)
@click.option('-n', '--instance-number', help='Instance number', required=False, default=None)
@click.option('-s', '--container-file', help='Container file', required=False, default=None)
@click.option('-d', '--disk-name', help='External disk name', required=False, default=None)
@click.option('-p', '--preemptible-override', help='Override preemptible setting', required=False, type=int, default=None)
@click.option('-m', '--machine-type', help='Machine type to use', required=False, default=None)
@click.option('-a', '--service-account', help='Additional service account to attach to runner', required=False, default=None)
@click.option('--ssh-tunnel-config', help="Base64 encoded SSH configuration file for establishing a tunnel", required=False, default=None)
@click.option('--ssh-tunnel-key', help="Base64 encoded private SSH key for establishing a tunnel", required=False, default=None)
@click.option('--secret-name', help="GCP Secret Manager secret name", required=False, default=None)
@click.option('--secret-namespace', help="GCP Secret Manager secret name", required=False, default=None)
@click.option('--zone', help="Google Cloud zone", required=False, default=None)
def main(mode, instance_number, container_file=None, disk_name=None, preemptible_override=None, machine_type=None, service_account=None, ssh_tunnel_config=None, ssh_tunnel_key=None, secret_name=None, secret_namespace=None, zone=None):
    check_mode_parameters(mode, instance_number, container_file)
    if mode == "create_vm":
        create_vm(instance_number, container_file, disk_name, preemptible_override, machine_type, service_account, ssh_tunnel_config, ssh_tunnel_key)
    elif mode == "delete_vm":
        delete_vm(instance_number)
    elif mode == "check-rsyslog":
        check_rsyslog(instance_number)
    elif mode == "detect_preempted_signal":
        detect_preempted_signal(instance_number)
    elif mode == "check_dmesg":
        check_dmesg(instance_number)
    elif mode == "delete_stale_instances":
        delete_stale_instances()
    elif mode == "get_secret":
        get_secret(secret_name, secret_namespace)
    elif mode == "get_project_id":
        print(PROJECT, PROJECT_ID)
    elif mode == "get_zones":
        print(get_available_zones())
    elif mode == "get_vm":
        print(describe_instance(instance_number))
    elif mode == "get_vms":
        print(list_auto_spawned_instances())
    elif mode == "get_disks":
        print(get_gcp_disks(disk_name))
    else:
        print(f"Unknown mode: {mode}! Exiting!")
        sys.exit(1)

if __name__ == '__main__':
    main()
