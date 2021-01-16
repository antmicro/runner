#!/bin/bash

set -e

WAN=$(route -n | grep ^0.0.0.0 | awk '{ print $8 "\t" }')
C='-m comment --comment qemu'
RANGE=172.17.0.2,172.17.0.100
WAN_IP=$(ip -f inet addr show $WAN | sed -En -e 's/.*inet ([0-9.]+).*/\1/p')
RUNNING_USER=$(whoami)
END=`expr $1 - 1`
DARGS=()

_term() {
	echo "Caught signal!"
	iptables-save | grep -v "qemu" | iptables-restore
	kill $(cat /var/run/dnsmasq-qemu.pid)
        for i in $(seq 0 $END); do ip link delete tap$i; done
	echo "Killing $!"
	kill -s TERM $!
	exit 0
}

trap _term INT TERM

echo "wan interface:	$WAN"
echo "running as:	$RUNNING_USER"
echo "wan ip:		$WAN_IP"

echo 1 > /proc/sys/net/ipv4/ip_forward
chmod 777 /dev/vhost-net
chmod 777 /dev/vhost-vsock

for i in $(seq 0 $END)
do
    GW=172.17.$i.1
    TAP=tap$i

    DARGS+=( "interface=$TAP" )
    DARGS+=( "dhcp-range=172.17.$i.2,172.17.$i.100" )

    echo "tap gw:		$GW"

    ip tuntap add dev $TAP mode tap
    ip a a $GW/24 dev $TAP
    ip link set dev $TAP up

    iptables -t nat -A POSTROUTING -o $WAN -j MASQUERADE --random $C
    iptables -A FORWARD -i $TAP -o $WAN -j ACCEPT $C
    iptables -A FORWARD -i $WAN -o $TAP -m conntrack --ctstate RELATED,ESTABLISHED -j ACCEPT $C
done

echo "Taps created."

dnsmasq \
	--strict-order \
	--except-interface=lo \
	--conf-file="" \
	--pid-file=/var/run/dnsmasq-qemu.pid \
	--dhcp-no-override \
	--dhcp-sequential-ip \
	--user=$USER \
        "${DARGS[@]/#/--}"

while [ 1 ]
do
	sleep 60 &
	wait $!
done

