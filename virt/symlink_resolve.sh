#!/bin/bash

set -e

cd $(dirname $0)

PREFIX=$1

sshSend() {
    echo "+ $@"
    /usr/bin/ssh -q \
        -o "UserKnownHostsFile /dev/null" \
        -o "StrictHostKeyChecking no" \
        scalerunner@172.17.$PREFIX.2 << EOF
sudo -s
$@
EOF
}

sshSend "mkdir -p /root/work"
sshSend "mount --bind /mnt/2/work /root/work"
sshSend 'cd /root/work;for f in $(find -type l);do cp -f $(readlink $f) $f || true;done;'
sshSend 'cd /root/work;find -type l -exec unlink {} \;'
sshSend "umount /root/work"
