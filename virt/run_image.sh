#!/bin/bash

set -e

cd $(dirname $0)

OVERLAY_SIZE=10G
CPU_COUNT=2
RAM=2G
SPECS_FILE="../.vm_specs"

if [ -f "$SPECS_FILE" ]; then
    echo "Using VM specs file."
    source $SPECS_FILE
else
    echo "VM specs file not found, using preset values."
fi

echo "Overlay size:     $OVERLAY_SIZE"
echo "vCPU count:       $CPU_COUNT"
echo "RAM:              $RAM"

while getopts ":n:r:s:" o; do
    case "${o}" in
        n)
            PREFIX=${OPTARG}
            ;;
        s)
            CONTAINER=${OPTARG}
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
SIF_FILE=$WORKDIR/sif/$CONTAINER.sif
FREE_SPACE=$(df -B1 . | tail -n +2 | awk '{ print $4 "\t" }')
DUMMY_DISK=$WORKDIR/small.img
SSH_PUB_KEY=$HOME/.ssh/id_rsa
SHARE_PATH=$(realpath ../_layout)/_work_${PREFIX}/_temp/_runner_file_commands

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

sshSend() {
    echo $@
    /usr/bin/ssh -q \
        -o "UserKnownHostsFile /dev/null" \
        -o "StrictHostKeyChecking no" \
        scalerunner@172.17.$PREFIX.2 << EOF
sudo -s
$@
EOF
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
	-kernel $WORKDIR/bzImage-2021-01-04--12-34-20 \
	-m $RAM -append "console=ttyS0" -enable-kvm -smp $CPU_COUNT -cpu host \
	-drive format=raw,file.filename=$SIF_FILE,file.locking=off,file.driver=file,snapshot=on \
	-drive format=raw,file.filename=$DUMMY_DISK,file.locking=off,file.driver=file,snapshot=on \
	-drive format=raw,file.filename=$DUMMY_DISK,file.locking=off,file.driver=file,snapshot=on \
	-drive format=raw,file=$OVERLAY_IMG \
	-nic tap,ifname=$TAP,script=no,downscript=no,model=virtio-net-pci \
	-smbios type=1,manufacturer=Antmicro,product="Antmicro Compute Engine",version="" \
	-smbios type=2,manufacturer=Antmicro,product="Antmicro Compute Engine",version="" \
	-smbios type=11,value="set_hostname scalenode-github-$PREFIX" \
	-smbios type=11,value="inject_key scalerunner:'$(cat ${SSH_PUB_KEY}.pub)'" \
        -fsdev local,id=share_dev,path=$SHARE_PATH,security_model=mapped-file \
        -device virtio-9p-pci,fsdev=share_dev,mount_tag=share_mount \
	-serial pipe:$Q \
	-monitor pipe:$Q2 \
	-pidfile $Q.pid \
	-display none \
	-daemonize \
	--enable-kvm


readUntilString "Welcome to Buildroot"

until nc -vzw 2 172.17.$PREFIX.2 22; do sleep 2; done

sshSend "mkdir -p /9p"
sshSend "mke2fs /dev/sdd"
sshSend "mount /dev/sdd /mnt"
sshSend "mkdir -p /mnt/1 /mnt/2/work /mnt/3"
sshSend "mount -t 9p -o trans=virtio,version=9p2000.L,msize=124288,cache=none share_mount /9p"
sshSend "singularity instance start -C -e --dns 8.8.8.8 --overlay /mnt/1 --bind /mnt/2:/root,/9p /tmp/container.sif i"
