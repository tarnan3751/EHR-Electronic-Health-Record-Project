#!/bin/sh
# Brings the primary's database up to date, then exits. Runs on every start of the local stack; every phase is
# safe to repeat. Connects as ehr_owner, whose credentials the running apps never get.
#   1. db/policies/00-roles.sql  the runtime roles, then their passwords from the environment
#   2. EF Core migrations        the schema from db/migrations, through the bundle built into this image
#   3. db/policies/*.sql         everything else (grants; later RLS and triggers), in file-name order
set -eu

export PGHOST="${PGHOST:-onprem-db}" PGDATABASE="${PGDATABASE:-ehr}" PGUSER=ehr_owner PGPASSWORD="$EHR_OWNER_PASSWORD"
sql() { psql -v ON_ERROR_STOP=1 --quiet --no-psqlrc "$@"; }

echo "Roles"
sql --file /policies/00-roles.sql
sql -v app_password="$EHR_APP_PASSWORD" -v read_password="$EHR_READ_PASSWORD" <<'SQL'
ALTER ROLE ehr_app PASSWORD :'app_password';
ALTER ROLE ehr_read PASSWORD :'read_password';
SQL

echo "Migrations"
# Npgsql takes the password from PGPASSWORD, so it isn't in the connection string.
EHR_MIGRATIONS_CONNECTION="Host=$PGHOST;Database=$PGDATABASE;Username=$PGUSER" efbundle

echo "Policies"
for file in /policies/*.sql; do
  [ "$file" = /policies/00-roles.sql ] && continue
  echo "  $(basename "$file")"
  sql --file "$file"
done

echo "Database is up to date"
