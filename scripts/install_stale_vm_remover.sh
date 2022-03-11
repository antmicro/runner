croncmd="cd $(dirname $(readlink -f "$0"))/../virt && python3 vm_command.py --mode delete_stale_instances"
cronjob="0 4 * * * $croncmd"
( crontab -l | grep -v -F "$croncmd" ; echo "$cronjob" ) | crontab -

