#!/usr/bin/python3
import os, sys, subprocess, json, click, datetime, platform
from collections import namedtuple

def load_config():
    with open('../.vm_specs.json', 'r') as f:
        return json.load(f, object_hook=lambda d: namedtuple('vm_specs', d.keys())(*d.values()))

@click.command()
@click.option('-n', '--instance-number', help='Instance number', required=True)
def main(instance_number):
    c = load_config()

    project_id = c.gcp.project
    zone = c.gcp.zone
    instance_name = f'{platform.node()}-auto-spawned{instance_number}'

    operation_list_cmd = f'gcloud compute operations list ' \
                         f'--project={project_id} ' \
                         f'--filter="zone={zone} AND targetLink.basename()={instance_name} AND operationType=compute.instances.preempted" ' \
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
        print('\n'+operation_list_cmd.replace(c.gcp.project, '***'))
        sys.exit(1)

if __name__ == '__main__':
    main()
