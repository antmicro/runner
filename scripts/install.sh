#!/bin/bash
set -e

## 71d532d

update-alternatives --install /usr/bin/python python /usr/bin/python3 50
apt -y install git
echo "%wheel         ALL = (ALL) NOPASSWD: ALL" >> /etc/sudoers.d/wheel_passwordless
groupadd -f wheel
useradd -G sudo,wheel,kvm runner
mkdir -p /home/runner
chown runner:runner /home/runner
sudo -u runner bash -c "cd /home/runner&&cat /dev/zero | ssh-keygen -q -N '' || true"
sudo -u runner git clone https://github.com/antmicro/runner.git -b vm-runners /home/runner/github-actions-runner
apt -y install htop iotop psmisc sshfs supervisor tmux vim rsync util-linux netcat-openbsd openssh-client python3-requests python3-click python3-paramiko unbound libicu-dev ncurses-term
systemctl stop unbound
systemctl disable unbound
sudo -u runner bash -c "cd /home/runner/github-actions-runner&&sudo ./install_systemd_services.sh"
ln -s /etc/apparmor.d/usr.sbin.unbound /etc/apparmor.d/disable/usr.sbin.unbound
bash -c "apparmor_parser -R /etc/apparmor.d/usr.sbin.unbound"
bash -c "systemctl stop gha-main@*"
sudo -u runner bash -c "cd /home/runner/github-actions-runner/src&&./dev.sh layout Debug"

