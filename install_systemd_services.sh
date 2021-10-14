#!/bin/bash

set -e

cd $(dirname $0)

if [ `whoami` != 'root' ]; then
    echo "Please run this script as root!"
    exit 1
fi

DESC="GitHub Actions GCP runner"

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
LimitAS=infinity
LimitRSS=infinity
LimitCORE=infinity
LimitNPROC=infinity
LimitNOFILE=infinity
TasksMax=infinity

[Install]
WantedBy=multi-user.target
\0
EOM

echo "$MAIN_SYSTEMD_UNIT" > /lib/systemd/system/gha-main@.service

systemctl daemon-reload
