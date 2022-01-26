#!/bin/bash

gcp_disk_name="$(hostname)--logs"
gcp_disk_size="10GB"

get_coordinator_zone() {
    curl -s "http://metadata.google.internal/computeMetadata/v1/instance/zone" \
        -H "Metadata-Flavor: Google" \
        | rev \
        | cut -d '/' -f1 \
        | rev
}

create_logs_disk() {
    gcloud compute disks create \
        --type=pd-standard \
        --zone=$(get_coordinator_zone) \
        --size=$gcp_disk_size \
        $gcp_disk_name
}

$@
