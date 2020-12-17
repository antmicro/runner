#!/bin/sh

TAP=tap0
WAN=$(/sbin/route -n | grep ^0.0.0.0 | awk '{ print $8 "\t" }')
GW=172.17.0.1
C='-m comment --comment "qemu"'
RANGE=172.17.0.2,172.17.0.100
WAN_IP=$(ip -f inet addr show $WAN | sed -En -e 's/.*inet ([0-9.]+).*/\1/p')
RUNNING_USER=$(whoami)

_term() {
	echo "Caught signal!"
	iptables-save | grep -v "qemu" | iptables-restore
	kill $(cat /var/run/dnsmasq-qemu.pid)
	ip link delete $TAP
	echo "Killing $!"
	kill -s TERM $!
	exit 0
}

trap _term INT TERM

echo "tap interface:	$TAP"
echo "wan interface:	$WAN"
echo "tap gw:		$GW"
echo "wan ip:		$WAN_IP"
echo "running as:	$RUNNING_USER"

/usr/bin/ip tuntap add dev $TAP mode tap
/sbin/ifconfig $TAP $GW up

echo 1 > /proc/sys/net/ipv4/ip_forward

/sbin/iptables -t nat -A POSTROUTING -o $WAN -j MASQUERADE $C
/sbin/iptables -I FORWARD 1 -i $TAP -j ACCEPT $C
/sbin/iptables -I FORWARD 1 -o $TAP -m state --state RELATED,ESTABLISHED -j ACCEPT $C

/usr/sbin/dnsmasq \
	--strict-order \
	--except-interface=lo \
	--interface=$TAP \
	--listen-address=$GW \
	--bind-interfaces \
	--dhcp-range=$RANGE \
	--conf-file="" \
	--pid-file=/var/run/dnsmasq-qemu.pid \
	--dhcp-no-override \
	--dhcp-sequential-ip \
	--user=$USER

while [ 1 ]
do
	sleep 60 &
	wait $!
done

