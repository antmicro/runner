#!/bin/bash

set -e

cd $(dirname $0)

WORKDIR=$(realpath work)
OVERLAY_IMG=$WORKDIR/overlay.img
SIF_FILE=$WORKDIR/debian10.sif
FREE_SPACE=$(df -B1 . | tail -n +2 | awk '{ print $4 "\t" }')
OVERLAY_SIZE=$((FREE_SPACE / 2))
DUMMY_DISK=$WORKDIR/small.img
SSH_PUB_KEY=$HOME/.ssh/id_rsa
RANDOM_MAC=$(printf '00-60-2F-%02X-%02X-%02X\n' $((RANDOM%256)) $((RANDOM%256)) $((RANDOM%256)))
SHARE_PATH=$(realpath ../_layout/_work/)

Q=$WORKDIR/qemu
SIN=$Q.in
SOUT=$Q.out

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

if [ -f "$OVERLAY_IMG" ]; then
	rm -rf $OVERLAY_IMG
fi

if [ ! -f "$SSH_PUB_KEY" ]; then
	ssh-keygen -t rsa -f $SSH_PUB_KEY -q -P ""
fi

if [ ! -f "$DUMMY_DISK" ]; then
	fallocate -l 1MB $DUMMY_DISK
fi

mkdir -p $SHARE_PATH

fallocate -l $OVERLAY_SIZE $OVERLAY_IMG

mkfifo $SIN $SOUT || true

qemu-system-x86_64 \
	-kernel $WORKDIR/bzImage-2020-12-17--14-22-23 \
	-m 4G -append "console=ttyS0" -enable-kvm -smp $(nproc) -cpu host \
	-drive format=raw,file=$SIF_FILE \
	-drive format=raw,file.filename=$DUMMY_DISK,file.locking=off,file.driver=file \
	-drive format=raw,file.filename=$DUMMY_DISK,file.locking=off,file.driver=file \
	-drive format=raw,file=$OVERLAY_IMG \
	-net nic \
	-net tap,ifname=tap0,script=no,downscript=no \
	-smbios type=1,manufacturer=Antmicro,product="Antmicro Compute Engine",version="" \
	-smbios type=2,manufacturer=Antmicro,product="Antmicro Compute Engine",version="" \
	-smbios type=11,value="set_hostname scalenode-github" \
	-smbios type=11,value="inject_key scalerunner:'$(cat ${SSH_PUB_KEY}.pub)'" \
	-fsdev local,id=share_dev,path=$SHARE_PATH,security_model=none \
	-device virtio-9p-pci,fsdev=share_dev,mount_tag=share_mount \
	-serial pipe:$Q \
	-pidfile $Q.pid \
	-display none \
	-daemonize \
	--enable-kvm


readUntilString "Welcome to Buildroot"

writeSer "scalerunner"
writeSer "scalerunner"
writeSer "sudo bash -c \"mke2fs /dev/sdd;mount /dev/sdd /mnt;mkdir /9p;mount -t 9p -o trans=virtio,version=9p2000.L share_mount /9p\""
writeSer "sudo singularity instance start -C --overlay /mnt --bind /9p /tmp/container.sif i"

readUntilString "instance started successfully"

writeSer "ip route get 1.2.3.4 | awk {'print \$7'} | sudo tee /9p/ip"

writeSer "exit"
