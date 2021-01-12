#!/bin/bash

set -e

cd $(dirname $0)

if [ `whoami` != 'root' ]; then
    echo "Please run this script as root!"
    exit 1
fi

TAP_PATH=$PWD/virt/tap.sh
DESC="GitHub Actions QEMU runner"

read -r -d '\0' NET_SYSTEMD_UNIT << EOM
[Unit]
Description=$DESC - network helper
AssertPathExists=$PWD/virt
Wants=network-online.target
After=network-online.target

[Service]
WorkingDirectory=$PWD
ExecStart=$TAP_PATH %i
KillMode=process

[Install]
WantedBy=multi-user.target
\0
EOM

read -r -d '\0' MAIN_SYSTEMD_UNIT << EOM
[Unit]
Description=$DESC - main runner
AssertPathExists=$PWD
Wants=network-online.target
After=network-online.target

[Service]
WorkingDirectory=$PWD
Environment=SCALE=%i
ExecStart=/usr/bin/supervisord -n -c $PWD/supervisord.conf
KillMode=process
User=$SUDO_USER

[Install]
WantedBy=multi-user.target
\0
EOM

echo "$MAIN_SYSTEMD_UNIT" > /lib/systemd/system/gha-main@.service
echo "$NET_SYSTEMD_UNIT" > /lib/systemd/system/gha-taps@.service

systemctl stop dnsmasq
systemctl disable dnsmasq
systemctl daemon-reload
