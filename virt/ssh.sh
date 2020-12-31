#!/bin/bash

IP=172.17.$1.2

echo "Connecting to $IP"

/usr/bin/ssh -q \
    -o "UserKnownHostsFile /dev/null" \
    -o "StrictHostKeyChecking no" \
    scalerunner@$IP -t "bash -c 'sudo singularity shell -e instance://i'"
