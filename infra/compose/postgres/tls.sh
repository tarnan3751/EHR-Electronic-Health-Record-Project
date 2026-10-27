#!/bin/sh
# Gives PostgreSQL the stack's certificate (infra/compose/certs) in a form it accepts: a copy of the key owned by
# postgres and readable by nobody else. The bind-mounted files keep the host's owner and mode, which PostgreSQL refuses
# for a private key. Then hands over to the command it was given: the image's entrypoint for the primary, the bootstrap
# script for the replica. Runs inside the postgres:18 container, as root, before PostgreSQL starts.
set -eu
install -d -o postgres -g postgres -m 700 /var/run/postgresql/tls
install -o postgres -g postgres -m 600 /certs/example.com-key.pem /var/run/postgresql/tls/server.key
install -o postgres -g postgres -m 644 /certs/example.com.pem /var/run/postgresql/tls/server.crt
exec "$@"
