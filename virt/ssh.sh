#!/bin/bash

# Parameters list:
# $1 -- hostname
# $2 -- verb
# $3 -- (optional) verb parameter

IP=$1

echo "Connecting to $IP"

if [ "$2" = "-s" ]; then
    CMD="-t bash -c 'sudo singularity shell -e instance://i'"
elif [ "$2" = "--sargraph-stop" ]; then
    CMD="-t bash -c 'sudo sargraph chart stop && sudo chmod 777 $3'"
elif [ "$2" = "--sargraph-label" ]; then
    CMD="-t bash -c 'sudo sargraph chart label \"$3\"'"
else
    CMD=""
fi

/usr/bin/ssh -q \
    -o "UserKnownHostsFile /dev/null" \
    -o "StrictHostKeyChecking no" \
    scalerunner@$IP ${CMD}
