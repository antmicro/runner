#!/bin/bash
LOG_ROOT_DIR="/var/log/runner/"
if ! mountpoint -q ${LOG_ROOT_DIR}; then
  LOG_ROOT_DIR="/home/runner/github-actions-runner/_layout/"
  if [[ ! -d $LOG_ROOT_DIR ]]; then
    echo "Couldn't find logs directory. Make sure script is properly configured!"
    exit 1
  fi
fi

function compress_logs() {
  DIR_ROTATE_DAYS=$1
  CURRENT_DATE=$(date '+%Y-%m-%d')

  echo "Compressing $LOG_ROOT_DIR files that are $DIR_ROTATE_DAYS days old...";
  for LOG_DIR in $(find $LOG_ROOT_DIR -maxdepth 1 -mindepth 1 -type d -name "*diag*"| sort); do
    LOG_FILES_TO_COMPRESS=$(find $LOG_DIR -maxdepth 1 -mindepth 1 -mtime +"$((DIR_ROTATE_DAYS - 1))" -name "*.log" | sort | tr '\n' ' ')
    FOLDER_NAME=$(basename $LOG_DIR)
    if [[ ! -z $LOG_FILES_TO_COMPRESS ]]; then
      if tar czf "$LOG_DIR/$FOLDER_NAME-${CURRENT_DATE}.tar.gz" $LOG_FILES_TO_COMPRESS --remove-files; then
	echo "Compression of $LOG_DIR done."
      else
	echo "Failed to compress: $LOG_DIR"
      fi
    else
	echo "Folder: $LOG_DIR doesn't have any files to compress, skipping"
    fi
  done
}

function delete_old_tarball() {
  TARBALL_DELETION_DAYS=$1
  echo "Removing $LOG_ROOT_DIR .tar.gz files that are $TARBALL_DELETION_DAYS days old..."
  for FILE in $(find $LOG_ROOT_DIR -maxdepth 2 -type f -mtime +"$((TARBALL_DELETION_DAYS - 1))" -name "*.tar.gz" | sort); do
    echo "Removing $FILE ... ";
    if rm "$FILE"; then
      echo "Success";
    else
      echo "Failed";
    fi
  done
}

CURRENT_DISC_USAGE=$(df $LOG_ROOT_DIR --output='pcent' | grep -o "[0-9]*")
echo "Current disc usage where logs are stored: $CURRENT_DISC_USAGE%"
DIR_ROTATE_DAYS=1

compress_logs $DIR_ROTATE_DAYS

if [ $CURRENT_DISC_USAGE -gt 70 ]; then
  echo "More then 70% of disc usage, deleting tars until atleast 50% is free"
  DELETION_DAYS=15
  while [ $CURRENT_DISC_USAGE -gt 50 ]; do
    if [ $DELETION_DAYS -eq 0 ]; then
      echo "Tried to delete all old tarballs, but disc usage is still higher than 50%! Please increase logs disc size!"
      exit 1
    fi
    delete_old_tarball $DELETION_DAYS
    DELETION_DAYS=$((DELETION_DAYS-1))
    CURRENT_DISC_USAGE=$(df $LOG_ROOT_DIR --output='pcent' | grep -o "[0-9]*")
    echo "Current disc usage where logs are stored: $CURRENT_DISC_USAGE%"
  done
else
  echo "Less than 70% of disc usage, not deleting old tars"
fi
