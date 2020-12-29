#!/bin/bash

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
    esac
done

cd _layout

for i in $(seq 0 $num); do
    GH_RUNNER_NUM=$i ./config.sh --url $url --token $token --unattended
done
