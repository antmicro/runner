#!/bin/bash

IP=172.17.$1.2

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
    scalerunner@$IP:/mnt/2 work/test

