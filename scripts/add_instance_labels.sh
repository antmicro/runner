#!/bin/bash

set -e

get_coordinator_zone() {
    curl -s "http://metadata.google.internal/computeMetadata/v1/instance/zone" \
        -H "Metadata-Flavor: Google" \
        | rev \
        | cut -d '/' -f1 \
        | rev
}

PID_PATH=${1:-/home/runner/github-actions-runner/virt/work/supervisord.pid}
GHA_MAIN_SERVICE_PID="$(cat $PID_PATH)"
COORDINATOR_BOOT_ID="$(cat /proc/sys/kernel/random/boot_id)"
COORDINATOR_NAME="$(hostname)"
COORDINATOR_ZONE="$(get_coordinator_zone)"

echo "Setting $COORDINATOR_NAME metadata: coordinator_pid=$GHA_MAIN_SERVICE_PID"
echo "Setting $COORDINATOR_NAME metadata: coordinator_boot_id=$COORDINATOR_BOOT_ID"

gcloud compute instances add-metadata \
    $COORDINATOR_NAME \
    --zone="$COORDINATOR_ZONE" \
    --metadata="coordinator_pid=$GHA_MAIN_SERVICE_PID,coordinator_boot_id=$COORDINATOR_BOOT_ID"

echo "Setting $COORDINATOR_NAME metadata: DONE"
