#!/bin/bash

function help() {
    echo "Usage: $0 [COMMAND] [INSTANCE_NUMBER]"
    echo ""
    echo "Where [COMMAND] is one of:"
    echo "   mount"
    echo "   umount"
    echo "   status"
    exit 1
}

function umount_retry() {
    timeout=30
    for i in $(seq $timeout); do
        echo "[$i] Trying to unmout $SHARE_PATH"
        fusermount -u $SHARE_PATH
        if [ $? -eq 0 ] || [ ! -f "$SHARE_PATH" ]; then
            break
        elif [ $i -ne $timeout ]; then
            processes_blocking=$(fuser $SHARE_PATH)
            echo "Unmount blocked by pid: $processes_blocking"
            echo "Retrying in 1s"
            sleep 1
        else
            # Timeout
            exit $?
        fi
    done
}

cd $(dirname $0)

if [ "$#" -ne 2 ] && [ "$#" -ne 3 ]; then
    help
fi

# Using the IP address is the preferred way of connecting.
# The DNS server might have an older value or there might be a temporary resolution failure.
if [ -n "$3" ]; then
    IP="$3"
else
    echo "WARNING: Hostname not supplied, inferring from instance number"
    IP="$(hostname)-auto-spawned$2"
fi

SHARE_PATH=$(realpath ../_layout)/_work_$2/
REMOTE_PATH="scalerunner@$IP:/mnt/2/"

mkdir -p $SHARE_PATH

case "$1" in
    mount)
        echo "Mounting $REMOTE_PATH at $SHARE_PATH"
        /usr/bin/sshfs \
            -o sftp_server="/usr/bin/sudo /usr/libexec/sftp-server" \
            -o Ciphers=aes128-gcm@openssh.com \
            -o Compression=no \
            -o UserKnownHostsFile=/dev/null \
            -o StrictHostKeyChecking=no \
            -o IdentityFile=~/.ssh/id_rsa \
            -o reconnect \
            -o uid=$UID \
            -o gid=$UID \
            $REMOTE_PATH $SHARE_PATH
        exit $?
        ;;
    umount)
        umount_retry $SHARE_PATH
        ;;
    status)
        mountpoint -q $SHARE_PATH
        MOUNT_STATUS=$?
        echo "$MOUNT_STATUS"
        exit $?
        ;;
    *)
        help
        ;;
esac
