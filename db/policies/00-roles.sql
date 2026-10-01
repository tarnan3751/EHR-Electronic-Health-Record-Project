-- Runtime roles. Applied as ehr_owner before migrations, on every run of infra/migrate; safe to re-run.
-- ehr_owner itself is created when the primary is first initialized (infra/compose/postgres/primary/initdb).
--
--   ehr_app   the on-prem app: reads and writes, through the grants in 10-grants.sql only
--   ehr_read  read-only: the cloud app against the replica, and on-prem reads
--
-- Neither owns anything, and both are created without SUPERUSER, CREATEDB, CREATEROLE, REPLICATION or
-- BYPASSRLS (PostgreSQL's defaults). Passwords are deployment configuration, not policy: infra/migrate sets
-- them from the environment.
DO $$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ehr_app') THEN
    CREATE ROLE ehr_app LOGIN;
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ehr_read') THEN
    CREATE ROLE ehr_read LOGIN;
  END IF;
END
$$;
