#!/bin/sh
# Executable on purpose (see 10-infra-roles.sh). Runs once, as the superuser, when the primary's volume is
# first created. Creates ehr_owner, the role migrations run as (infra/migrate, CI and deploy; never the running
# apps), and the ehr schema it owns.
# ehr_owner may create roles: it manages the runtime roles defined in db/policies. PostgreSQL stops it from
# creating or granting SUPERUSER, REPLICATION or BYPASSRLS, and superuser logins over the network stay refused.
set -eu

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v owner_password="$EHR_OWNER_PASSWORD" <<'SQL'
CREATE ROLE ehr_owner WITH LOGIN CREATEROLE PASSWORD :'owner_password';
CREATE SCHEMA ehr AUTHORIZATION ehr_owner;
SQL
