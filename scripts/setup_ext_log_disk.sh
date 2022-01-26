#!/bin/bash

# Disk paths/names within the system
disk_device_name="gharunnerlogs"
disk_path="/dev/disk/by-id/google-$disk_device_name"
disk_first_part_path="$disk_path-part1"
disk_mount_path="/var/log/runner"

# Disk names within Google Cloud Platform
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
        --zone="$(get_coordinator_zone)" \
        --size="$gcp_disk_size" \
        "$gcp_disk_name"
}

# WARNING: this function requires that the SA attached to the coordinator has the following role:
# - iam.serviceAccountUser
attach_logs_disk() {
    gcloud compute instances attach-disk "$(hostname)" \
        --disk="$gcp_disk_name" \
        --device-name="$disk_device_name" \
        --zone="$(get_coordinator_zone)" \
        --mode=rw
}

is_first_part_formatted() {
    sudo blkid -s TYPE | grep "$(realpath $disk_path)" | wc -l
}

format_logs_disk() {
    if [ ! -b "$disk_first_part_path" ]; then
        (echo 'n'; echo 'p'; echo '1'; echo; echo; echo 't'; echo '0b'; echo 'w';) \
            | sudo fdisk --wipe always --wipe-partition always "$disk_path" && sync
    fi

    if [ "$(is_first_part_formatted)" -ne 1 ]; then
        sudo mkfs.ext4 -O ^has_journal "$disk_first_part_path"
    fi
}

$@
