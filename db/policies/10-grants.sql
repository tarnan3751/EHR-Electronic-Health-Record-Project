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

-- Staff and patient accounts, with Identity's claim, login and token tables (Accounts/ in Ehr.Data). Internal: grants
-- only, no row-level security, because sign-in has to find the account before anyone is signed in. No DELETE:
-- accounts are disabled, never removed, and the app calls none of Identity's remove-claim, remove-login or
-- remove-token methods.
GRANT SELECT, INSERT, UPDATE ON ehr.staff_users, ehr.staff_user_claims, ehr.staff_user_logins, ehr.staff_user_tokens TO ehr_app;
GRANT SELECT ON ehr.staff_users, ehr.staff_user_claims, ehr.staff_user_logins, ehr.staff_user_tokens TO ehr_read;
GRANT SELECT, INSERT, UPDATE ON ehr.patient_users, ehr.patient_user_claims, ehr.patient_user_logins, ehr.patient_user_tokens TO ehr_app;
GRANT SELECT ON ehr.patient_users, ehr.patient_user_claims, ehr.patient_user_logins, ehr.patient_user_tokens TO ehr_read;

-- Patients. PHI: row-level security is enabled and forced, and every write is recorded by trigger, in the policy
-- files that follow. No DELETE: patients are never removed.
GRANT SELECT, INSERT, UPDATE ON ehr.patients TO ehr_app;
GRANT SELECT ON ehr.patients TO ehr_read;

-- Patient assignments: which patients are on a clinician's panel. PHI, like patients: row-level security and the
-- write-audit trigger follow. An assignment ends (UPDATE); it is never deleted.
GRANT SELECT, INSERT, UPDATE ON ehr.patient_assignments TO ehr_app;
GRANT SELECT ON ehr.patient_assignments TO ehr_read;

-- Emergency access to a chart off the clinician's panel, with its reason. PHI, like patients: row-level security and
-- the write-audit trigger follow. Written once: no UPDATE and no DELETE.
GRANT SELECT, INSERT ON ehr.break_glass_accesses TO ehr_app;
GRANT SELECT ON ehr.break_glass_accesses TO ehr_read;

-- Portal enrollment codes, as hashes. Internal: grants only, no row-level security, because registration looks a
-- code up before anyone is signed in; the hash is useless without the code, which is never stored. The app marks a
-- code redeemed (UPDATE). No DELETE.
GRANT SELECT, INSERT, UPDATE ON ehr.patient_enrollments TO ehr_app;
GRANT SELECT ON ehr.patient_enrollments TO ehr_read;

-- Audit events. Restricted: row-level security is enabled and forced, in the policy files that follow; no trigger,
-- since nothing audits the audit. Append-only: no UPDATE and no DELETE for anyone at runtime. The write-audit
-- trigger inserts as the table's owner; the app role inserts read and sign-in events.
GRANT SELECT, INSERT ON ehr.audit_events TO ehr_app;
GRANT SELECT ON ehr.audit_events TO ehr_read;

COMMIT;
