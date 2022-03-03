croncmd="$(dirname $(readlink -f "$0"))/compress_log_files.sh"
cronjob="0 3 * * * $croncmd"
( crontab -l | grep -v -F "$croncmd" ; echo "$cronjob" ) | crontab -
