#!/bin/sh
# Keeps WAL streaming from the primary and takes a base backup when none exists, then every 23 hours, so the retention
# policy (onprem.conf) always has newer backups to keep in place of the old ones, which `barman cron` deletes with the
# WAL before them; and deletes a base backup that failed once it is a day old, since Barman's retention never does and
# it keeps whatever it had copied. Barman only connects out; nothing in this container listens.
set -eu

# Seconds between base backups: 23 hours, so with the 6-day window nothing older than 7 days can be restored
# (infra/compose/README.md), or less for CI's check that old backups are deleted (compose.backup-drill.yaml).
every=${BARMAN_BACKUP_EVERY:-82800}
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
  backups=$(barman list-backups onprem 2>/dev/null || true)
  # A failed base backup is listed as "onprem <id> - F - FAILED"; its ID is when it began, in this container's clock.
  for id in $(printf '%s\n' "$backups" | sed -n 's/^onprem \([0-9]\{8\}T[0-9]\{6\}\) .*- FAILED$/\1/p'); do
    began=$(date -d "$(printf '%s\n' "$id" | sed 's/^\(....\)\(..\)\(..\)T\(..\)\(..\)\(..\)$/\1-\2-\3 \4:\5:\6/')" +%s) || continue
    if [ "$((now - began))" -ge 86400 ]; then
      echo "Deleting base backup $id, which failed more than a day ago"
      barman delete onprem "$id" || true
    fi
  done
  if [ -z "$backups" ] || [ "$now" -ge "$(($(cat "$last" 2>/dev/null || echo 0) + every))" ]; then
    echo "Taking a base backup"
    barman switch-wal --force --archive onprem || true
    if barman backup --wait onprem; then
      echo "$now" > "$last"
    fi
  fi
  sleep 30
done
