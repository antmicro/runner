#!/bin/bash

cd $(dirname $0)

IP=172.17.$1.2
SHARE_PATH=$(realpath ../_layout)/_work_$1/$2
REMOTE_PATH="scalerunner@$IP:/mnt/2/work"

mkdir -p $SHARE_PATH

mountpoint -q $SHARE_PATH
MOUNT_STATUS=$?

if [ $MOUNT_STATUS -eq 1 ]; then
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
else
    echo "Unmouting $SHARE_PATH"
    fusermount -u $SHARE_PATH
fi
