#!/bin/sh
# Keeps WAL streaming from the primary and takes a base backup when none exists, then once a day, so the retention
# policy (onprem.conf) always has newer backups to keep in place of the old ones, which `barman cron` deletes with the
# WAL before them. Barman only connects out; nothing in this container listens.
set -eu

# Seconds between base backups: a day, or less for CI's check that old backups are deleted (compose.backup-drill.yaml).
every=${BARMAN_BACKUP_EVERY:-86400}
# When the last base backup this loop took began, in seconds since the epoch, kept with the backups.
last=/var/lib/barman/last-base-backup

if [ "$(id -u)" = 0 ]; then
  install -d -o postgres -g postgres -m 700 /var/lib/barman /var/log/barman
  touch /var/log/barman/barman.log
  chown postgres:postgres /var/log/barman/barman.log
  (umask 077 && printf '*:*:*:barman:%s\n' "$BARMAN_PASSWORD" > "$PGPASSFILE")
  chown postgres:postgres "$PGPASSFILE"
  tail -F /var/log/barman/barman.log &
  exec gosu postgres sh "$0" "$@"
fi

while :; do
  # Starts pg_receivewal if it isn't running, and applies the retention policy: deletes the backups it no longer needs
  # and the WAL from before the oldest one it keeps.
  barman -q cron || true
  now=$(date +%s)
  if [ -z "$(barman list-backups --minimal onprem 2>/dev/null)" ] ||
    [ "$now" -ge "$(($(cat "$last" 2>/dev/null || echo 0) + every))" ]; then
    echo "Taking a base backup"
    barman switch-wal --force --archive onprem || true
    if barman backup --wait onprem; then
      echo "$now" > "$last"
    fi
  fi
  sleep 30
done
