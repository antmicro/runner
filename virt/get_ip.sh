#!/bin/bash

set -e

cd $(dirname $0)

WORKDIR=$(realpath work)

Q2=$WORKDIR/qemu_mon
MIN=$Q2.in
MOUT=$Q2.out

LINE=""

function flushPipe() {
    # Ensure there is no lingering output in the pipe
    # by flushing it in a slightly hacky way.
    # dd complains about resource not being available....
    # dd: error reading 'work/qemu_mon.out': Resource temporarily unavailable
    # ...but it doesn't matter.
    (dd if=$MOUT iflag=nonblock of=/dev/null || true) >/dev/null 2>&1
}

flushPipe

echo 'info network' > $MIN

# Reading twice cause first line is echoed command.
read LINE < $MOUT
read LINE < $MOUT

flushPipe

MAC=$(echo $LINE | cut -d "," -f4 | cut -d "=" -f2)

echo $MAC
