#!/bin/sh
# First start only: clone the primary through the WAN link, then run as its hot standby.
# pg_basebackup --write-recovery-conf leaves standby.signal and the connection settings behind.
set -eu

if [ ! -s "$PGDATA/PG_VERSION" ]; then
  mkdir -p "$PGDATA"
  chown postgres:postgres "$PGDATA"
  chmod 700 "$PGDATA"
  # The connection settings, TLS included, are written into the standby's configuration for its streaming afterwards.
  until PGPASSWORD="$REPLICATOR_PASSWORD" gosu postgres pg_basebackup \
      --dbname="host=wan port=5432 user=replicator sslmode=verify-full sslrootcert=/certs/rootCA.pem" \
      --pgdata="$PGDATA" --wal-method=stream --slot=cloud_replica \
      --write-recovery-conf --checkpoint=fast --progress; do
    echo "Primary not reachable yet; retrying in 3 seconds"
    find "$PGDATA" -mindepth 1 -delete
    sleep 3
  done
fi

exec docker-entrypoint.sh "$@"
