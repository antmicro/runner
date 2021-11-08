#!/usr/bin/python3
import os, sys, subprocess, json, click, datetime, platform
from collections import namedtuple

def load_config():
    with open('../.vm_specs.json', 'r') as f:
        return json.load(f, object_hook=lambda d: namedtuple('vm_specs', d.keys())(*d.values()))

def check_preempted(current_log):
    for line in current_log:
        if "VM shutting down" in line:
            return line[line.find("VM shutting down"):]
    return None

@click.command()
@click.option('-n', '--instance-number', help='Instance number', required=True)
def main(instance_number):
    c = load_config()

    project_id = c.gcp.project
    zone = c.gcp.zone
    instance_name = f'{platform.node()}-auto-spawned{instance_number}.c.{project_id}.internal'

    gh_env_list = ["GITHUB_JOB_FULL", "GITHUB_SHA", "GITHUB_RUN_ID"]

    labels = ','.join(["{}={}".format(e.lower(), (os.environ.get(e) or 'null')[:63].lower()) for e in gh_env_list])

    current_log = []
    found_labels = False

    for line in reversed(list(open(f"work/{instance_name}.log"))):
        if labels in line.rstrip():
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

if __name__ == '__main__':
    main()
