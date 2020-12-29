#!/bin/bash

set -e

cd $(dirname $0)

while getopts ":n:r:" o; do
    case "${o}" in
        n)
            PREFIX=${OPTARG}
            ;;
        r)
            SHARE_SUFFIX=${OPTARG}
            ;;
        *)
            echo "Provide all arguments!"
            exit 1
            ;;
    esac
done
shift $((OPTIND-1))

WORKDIR=$(realpath work)
OVERLAY_IMG=$WORKDIR/${PREFIX}_overlay.img
SIF_FILE=$WORKDIR/debian10.sif
FREE_SPACE=$(df -B1 . | tail -n +2 | awk '{ print $4 "\t" }')
OVERLAY_SIZE=5G
DUMMY_DISK=$WORKDIR/small.img
SSH_PUB_KEY=$HOME/.ssh/id_rsa
SHARE_PATH=$(realpath ../_layout)/_work_${PREFIX}/${SHARE_SUFFIX}

TAP=tap${PREFIX}

Q=$WORKDIR/${PREFIX}_qemu
Q2=$WORKDIR/${PREFIX}_qemu_mon
SIN=$Q.in
SOUT=$Q.out
MIN=$Q2.in
MOUT=$Q2.out

echo "Instance number:  ${PREFIX}"
echo "Share path:       ${SHARE_PATH}"

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

mkfifo $SIN $SOUT $MIN $MOUT || true

qemu-system-x86_64 \
	-kernel $WORKDIR/bzImage-2020-12-24--22-09-40 \
	-m 4G -append "console=ttyS0" -enable-kvm -smp $(nproc) -cpu host \
	-drive format=raw,file.filename=$SIF_FILE,file.locking=off,file.driver=file,snapshot=on \
	-drive format=raw,file.filename=$DUMMY_DISK,file.locking=off,file.driver=file,snapshot=on \
	-drive format=raw,file.filename=$DUMMY_DISK,file.locking=off,file.driver=file,snapshot=on \
	-drive format=raw,file=$OVERLAY_IMG \
	-nic tap,ifname=$TAP,script=no,downscript=no,model=virtio-net-pci \
	-smbios type=1,manufacturer=Antmicro,product="Antmicro Compute Engine",version="" \
	-smbios type=2,manufacturer=Antmicro,product="Antmicro Compute Engine",version="" \
	-smbios type=11,value="set_hostname scalenode-github" \
	-smbios type=11,value="inject_key scalerunner:'$(cat ${SSH_PUB_KEY}.pub)'" \
	-fsdev local,id=share_dev,path=$SHARE_PATH,security_model=none \
	-device virtio-9p-pci,fsdev=share_dev,mount_tag=share_mount \
	-serial pipe:$Q \
	-monitor pipe:$Q2 \
	-pidfile $Q.pid \
	-display none \
	-daemonize \
	--enable-kvm


readUntilString "Welcome to Buildroot"

writeSer "scalerunner"
writeSer "scalerunner"
writeSer "sudo bash -c \"mke2fs /dev/sdd;mount /dev/sdd /mnt;mkdir /9p;mount -t 9p -o trans=virtio,version=9p2000.L share_mount /9p\""
writeSer "sudo singularity instance start -C -e --dns 8.8.8.8 --overlay /mnt --bind /9p /tmp/container.sif i"

readUntilString "instance started successfully"

#writeSer "ip route get 1.2.3.4 | awk {'print \$7'} | sudo tee /9p/ip"

writeSer "exit"
