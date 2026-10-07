-- Table privileges for the runtime roles. Applied as ehr_owner after migrations, on every run of
-- infra/migrate; safe to re-run. Every table is listed here explicitly: a new table gets no runtime access
-- until it's added, and Ehr.Security.Tests fails until it is.
-- The migrations history table is deliberately absent: only ehr_owner touches it.
--
-- This file is the complete list. It first removes every privilege the runtime roles hold in the ehr schema,
-- so one granted by hand anywhere else is gone after the next run. Both steps run in one transaction, so the
-- apps never see a moment without access.

BEGIN;

REVOKE ALL ON ALL TABLES IN SCHEMA ehr FROM ehr_app, ehr_read;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA ehr FROM ehr_app, ehr_read;
REVOKE ALL ON SCHEMA ehr FROM ehr_app, ehr_read;

GRANT USAGE ON SCHEMA ehr TO ehr_app, ehr_read;

-- Quick text, the walking skeleton's feature. No DELETE: phrases can't be deleted yet.
GRANT SELECT, INSERT, UPDATE ON ehr.quick_texts TO ehr_app;
GRANT SELECT ON ehr.quick_texts TO ehr_read;

-- Data Protection keys: added only by the on-prem app; both app hosts read them.
GRANT SELECT, INSERT ON ehr.data_protection_keys TO ehr_app;
GRANT SELECT ON ehr.data_protection_keys TO ehr_read;

-- Sync's change log: sync pulls read it on both servers. Only its trigger writes it (20-change-log.sql).
GRANT SELECT ON ehr.change_log TO ehr_app, ehr_read;

-- Sync pushes already applied: pushes are saved on-prem only, so the read role gets nothing.
GRANT SELECT, INSERT ON ehr.sync_operations TO ehr_app;

COMMIT;
