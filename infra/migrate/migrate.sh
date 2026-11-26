#!/bin/sh
# Brings the primary's database up to date, then exits. Runs on every start of the local stack; every phase is
# safe to repeat. Connects as ehr_owner, whose credentials the running apps never get, over TLS when PGSSLROOTCERT
# names the root to verify the primary against (the stack sets it; psql reads it by itself).
#   1. db/policies/00-roles.sql  the runtime roles, then their passwords from the environment
#   2. EF Core migrations        the schema from db/migrations, through the bundle built into this image
#   3. db/policies/*.sql         everything else (grants, row-level security, triggers), in file-name order
#   4. db/reference/*.sql        the reference data (standard codes, the order catalog, drug classes), in file-name
#                                order, then db/reference/ddinter/*.sql, which has a license of its own
#   5. the lab's key             the SHA-256 of EHR_LAB_KEY, or none when it is unset or empty (db/policies/76-lab.sql)
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
ssl=""
[ -n "${PGSSLROOTCERT:-}" ] && ssl=";SSL Mode=VerifyFull;Root Certificate=$PGSSLROOTCERT"
# A migration that moves data (VitalSigns, VitalCorrections, VitalReadings) is one command: ten minutes, where the driver
# allows 30 s.
EHR_MIGRATIONS_CONNECTION="Host=$PGHOST;Database=$PGDATABASE;Username=$PGUSER;Command Timeout=600$ssl" efbundle

echo "Policies"
for file in /policies/*.sql; do
  [ "$file" = /policies/00-roles.sql ] && continue
  echo "  $(basename "$file")"
  sql --file "$file"
done

echo "Reference data"
for file in /reference/*.sql /reference/ddinter/*.sql; do
  echo "  $(basename "$file")"
  sql --file "$file"
done

# Only the key's hash reaches the database, worked out here, so the key is never in a statement the server could log.
# Without a key no hash is stored, and the lab's door refuses every call.
echo "Lab key"
lab_key_hash=""
if [ -n "${EHR_LAB_KEY:-}" ]; then
  lab_key_hash=$(printf '%s' "$EHR_LAB_KEY" | sha256sum | cut -d ' ' -f 1)
  [ "${#lab_key_hash}" -eq 64 ] || { echo "The lab key's hash couldn't be worked out" >&2; exit 1; }
  echo "  set"
else
  echo "  none: EHR_LAB_KEY is not set, so no lab is connected"
fi
sql -v hash="$lab_key_hash" --output /dev/null <<'SQL'
SELECT ehr.set_lab_key(:'hash');
SQL

echo "Database is up to date"
