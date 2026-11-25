# Barman plus the PostgreSQL 18 client tools it drives (pg_basebackup, pg_receivewal).
# Built on postgres:18 so those tools always match the server, and so Barman runs as the same
# postgres user (uid 999) that owns any cluster it restores.
FROM postgres:18

# Barman runs `file` on every WAL file it archives, and `barman recover` copies with rsync,
# so both are installed explicitly.
# Barman is pinned to the version the stack and CI have run with, from the PostgreSQL project's repository, which keeps
# earlier versions beside the newest. Moving it is a change of its own: check that what the loop (entrypoint.sh), CI's
# stack job and the restore drill rely on still holds (the retention policy, and `list-backups` and `show-backup`
# output) in the stack before raising it. The version names the Debian release the postgres:18 image is built on, so a
# new release of that image fails this build too, for the same check.
ARG BARMAN_VERSION=3.20.1-1.pgdg13+1
RUN apt-get update \
 && apt-get install -y --no-install-recommends "barman=$BARMAN_VERSION" "python3-barman=$BARMAN_VERSION" file rsync \
 && rm -rf /var/lib/apt/lists/*

# Written from BARMAN_PASSWORD at start-up; set here so `docker compose exec` sessions use it too.
ENV PGPASSFILE=/tmp/barman.pgpass

COPY barman.conf /etc/barman.conf
COPY onprem.conf /etc/barman.d/onprem.conf
COPY entrypoint.sh /usr/local/bin/barman-entrypoint.sh

ENTRYPOINT ["/bin/sh", "/usr/local/bin/barman-entrypoint.sh"]
