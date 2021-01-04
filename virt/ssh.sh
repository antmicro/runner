#!/bin/bash

IP=172.17.$1.2

echo "Connecting to $IP"

if [ "$2" = "-s" ]; then
    SINGULARITY_CMD="-t bash -c 'sudo singularity shell -e instance://i'"
else
    SINGULARITY_CMD=""
fi

/usr/bin/ssh -q \
    -o "UserKnownHostsFile /dev/null" \
    -o "StrictHostKeyChecking no" \
    scalerunner@$IP ${SINGULARITY_CMD}
