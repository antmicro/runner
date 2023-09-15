#!/bin/bash

remove=0

while test -n "$1"; do
    case "$1" in
        "--url")
            url="$2"
            shift 2
            ;;
        "--token")
            token="$2"
            shift 2
            ;;
        "--num")
            num="$2"
            shift 2
            ;;
        "remove")
            remove=1
            shift 1
            ;;
    esac
done

cd _layout

num=`expr $num - 1`

for i in $(seq 0 $num); do
    if [ "$remove" -eq 1 ]; then
        GH_RUNNER_NUM=$i ./config.sh remove --token $token --unattended
    else
        GH_RUNNER_NUM=$i ./config.sh --url $url --token $token --unattended
    fi
done
