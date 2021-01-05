#!/bin/bash

set -e

cd $(dirname $0)

MISC_DIR=misc
SHARE_DIR=$MISC_DIR/share

SIN=$MISC_DIR/qemu.in
SOUT=$MISC_DIR/qemu.out
DISK=$MISC_DIR/qemu.raw
DUMMY_DISK=$MISC_DIR/qemu_small.raw
SSH_PUB_KEY=$HOME/.ssh/id_rsa

if [ ! -p "$SIN" ]; then
    mkfifo $SIN
fi

if [ ! -p "$SOUT" ]; then
    mkfifo $SOUT
fi

if [ -f "$DISK" ]; then
    rm -rf $DISK
fi

fallocate -l 2GB $DISK

if [ ! -f "$DUMMY_DISK" ]; then
    fallocate -l 1MB $DUMMY_DISK
fi

mkdir -p $SHARE_DIR

readUntilString() {
	echo "Reading serial until: $1"
	while read line; do
		echo "${line}"
		if [[ ${line} == *"$1"* ]]; then
			echo "Not reading serial anymore."
			break;
		fi
	done < $SOUT
}

writeSer() {
	echo $1 > $SIN
	sleep 0.2
}

qemu-system-x86_64 \
	-kernel $MISC_DIR/bzImage-2021-01-04--12-34-20 \
	-m 2G -append "console=ttyS0" -enable-kvm -smp 4 -cpu host \
        -drive format=raw,file.filename=$DUMMY_DISK,file.locking=off,file.driver=file,snapshot=on \
        -drive format=raw,file.filename=$DUMMY_DISK,file.locking=off,file.driver=file,snapshot=on \
        -drive format=raw,file.filename=$DUMMY_DISK,file.locking=off,file.driver=file,snapshot=on \
	-drive format=raw,file=$DISK \
	-smbios type=1,manufacturer=Antmicro,product="Antmicro Compute Engine",version="" \
	-smbios type=2,manufacturer=Antmicro,product="Antmicro Compute Engine",version="" \
	-smbios type=11,value="set_hostname speed-test" \
	-smbios type=11,value="inject_key scalerunner:'$(cat ${SSH_PUB_KEY}.pub)'" \
	-fsdev local,id=share_dev,path=$SHARE_DIR,security_model=mapped-file \
	-device virtio-9p-pci,fsdev=share_dev,mount_tag=share_mount \
	-serial pipe:$MISC_DIR/qemu \
	-pidfile $MISC_DIR/qemu.pid \
	-display none \
	-daemonize \
	--enable-kvm

FINISH="Done..."
MSIZE=124288

readUntilString "Welcome to Buildroot"

writeSer "scalerunner"
writeSer "scalerunner"
writeSer "export FINISH=\"$FINISH\""
writeSer "sudo bash -c \"mke2fs /dev/sdd;mount /dev/sdd /mnt;mkdir /9p;mount -t 9p -o trans=virtio,version=9p2000.L,msize=$MSIZE,cache=none share_mount /9p\" && echo \$FINISH"

readUntilString "$FINISH"

tput setaf 1
echo "After the download has finished, press ENTER to kill cat."
tput setaf 0

cat $SOUT &

writeSer "cd /9p && sudo curl -O https://speed.hetzner.de/1GB.bin && cd /mnt && sudo curl -O https://speed.hetzner.de/1GB.bin"

read -p "Press enter to continue"
pkill -P $$
