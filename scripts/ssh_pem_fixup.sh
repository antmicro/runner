#!/bin/sh

ssh-keygen -p -m pem \
    -f $HOME/.ssh/id_rsa \
    -P "" -N ""
