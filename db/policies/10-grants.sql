-- Table privileges for the runtime roles. Applied as ehr_owner after migrations, on every run of
-- infra/migrate; safe to re-run. Every table is listed here explicitly: a new table gets no runtime access
-- until it's added, and Ehr.Security.Tests fails until it is.
-- The migrations history table is deliberately absent: only ehr_owner touches it.

GRANT USAGE ON SCHEMA ehr TO ehr_app, ehr_read;

-- Quick text, the walking skeleton's feature. No DELETE: phrases can't be deleted yet.
GRANT SELECT, INSERT, UPDATE ON ehr.quick_texts TO ehr_app;
GRANT SELECT ON ehr.quick_texts TO ehr_read;

-- Data Protection keys: added only by the on-prem app; both app hosts read them.
GRANT SELECT, INSERT ON ehr.data_protection_keys TO ehr_app;
GRANT SELECT ON ehr.data_protection_keys TO ehr_read;
