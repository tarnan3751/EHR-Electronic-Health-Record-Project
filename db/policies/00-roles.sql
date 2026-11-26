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

-- A session of either role left idle in an open transaction is ended after a minute, and its transaction rolled back:
-- one that has written something would otherwise hold back the lab's hand-over (76-lab.sql) until it ended. The apps
-- never leave a transaction idle that long: a request's transaction holds database work only, and every call out of the
-- app (the dictation service, forwarding to the on-prem app, mail) is made outside one (the expansion plan's step 21).
-- A session ended this way fails at its next command, and Npgsql drops the connection. ehr_owner (migrations, the seed)
-- and the superuser aren't limited, and a statement still running isn't idle. Role settings are catalog rows, so the
-- replica's ehr_read sessions have it too.
ALTER ROLE ehr_app SET idle_in_transaction_session_timeout = '60s';
ALTER ROLE ehr_read SET idle_in_transaction_session_timeout = '60s';
