#!/bin/sh

if [[ ! -f $HOME/.ssh/id_rsa ]] || [[ ! -f $HOME/.ssh/id_rsa.pub ]] ; then
        ssh-keygen -q -N '' -f $HOME/.ssh/id_rsa
fi
