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

-- The desktop app's sessions, by token hash (Accounts/DesktopSession.cs). Internal, like the account tables: every
-- sync request looks its session up before anyone is signed in; the cloud app does so against the replica. The on-prem
-- app creates sessions and revokes them at sign-out (UPDATE). No DELETE.
GRANT SELECT, INSERT, UPDATE ON ehr.desktop_sessions TO ehr_app;
GRANT SELECT ON ehr.desktop_sessions TO ehr_read;

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

-- The rest of the chart: allergies, medications, diagnoses (conditions) and visits (encounters). PHI, like patients:
-- row-level security and the write-audit trigger follow. The on-prem app reads them as ehr_app, the cloud app as
-- ehr_read. Clinicians add and change allergies, medications and diagnoses, and mark them inactive: INSERT and UPDATE
-- for ehr_app, and never DELETE. Visits are still read only.
GRANT SELECT ON ehr.allergy_intolerances, ehr.medication_statements, ehr.conditions, ehr.encounters TO ehr_app, ehr_read;
GRANT INSERT, UPDATE ON ehr.allergy_intolerances, ehr.medication_statements, ehr.conditions TO ehr_app;

-- The history of changes to those three lists. PHI: row-level security and the write-audit trigger follow. Read only
-- for both roles: its rows are written by the database's own trigger, which runs as its owner (65-chart-lists.sql).
GRANT SELECT ON ehr.chart_history TO ehr_app, ehr_read;

-- The codes a chart entry can be picked from (ICD-10-CM, RxNorm, UNII). Not PHI: grants only. Read by both apps;
-- nothing at runtime changes it. Its rows are db/reference/*.sql, applied as ehr_owner after these files.
GRANT SELECT ON ehr.clinical_codes TO ehr_app, ehr_read;

-- The rest of the reference data, none of it about a patient: what a result or vital sign is and this clinic's range
-- for it, which results an order produces, the PHQ-9 and GAD-7 questionnaires, what each drug product is made of, the
-- classes its ingredients belong to and which of those cross-react, and known interactions between ingredients. Read
-- by both apps; nothing at runtime changes any of it. Its rows are db/reference's generated SQL.
GRANT SELECT ON ehr.observation_codes, ehr.order_result_codes, ehr.questionnaires, ehr.drug_ingredients, ehr.drug_classes,
    ehr.drug_class_cross_reactions, ehr.drug_interactions TO ehr_app, ehr_read;

-- Test results (observations). PHI, like the chart: row-level security and the write-audit trigger follow. Read only:
-- results will come from the lab interface, and no page changes them. The portal reads them on either server.
GRANT SELECT ON ehr.observations TO ehr_app, ehr_read;

-- Portal enrollment codes, as hashes. Internal: grants only, no row-level security, because registration looks a
-- code up before anyone is signed in; the hash is useless without the code, which is never stored. The app marks a
-- code redeemed (UPDATE). No DELETE.
GRANT SELECT, INSERT, UPDATE ON ehr.patient_enrollments TO ehr_app;
GRANT SELECT ON ehr.patient_enrollments TO ehr_read;

-- Audit events. Restricted: row-level security is enabled and forced, in the policy files that follow; no trigger,
-- since nothing audits the audit. Append-only: no UPDATE and no DELETE for anyone at runtime. The write-audit
-- trigger inserts as the table's owner; the app role inserts read and sign-in events.
GRANT INSERT ON ehr.audit_events TO ehr_app;
-- Every column but details, which holds the values a write changed: admins get those through
-- ehr.shown_audit_details (40-audit.sql), which never gives a sensitive item's.
GRANT SELECT (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source, reason, sensitive) ON ehr.audit_events TO ehr_app, ehr_read;

GRANT SELECT, INSERT, UPDATE ON ehr.clinical_drafts TO ehr_app;

-- Final clinical records are append-only. Cloud reads remain subject to the same forced RLS.
GRANT SELECT ON ehr.clinical_drafts TO ehr_read;
GRANT SELECT, INSERT ON ehr.clinical_notes TO ehr_app;
GRANT SELECT ON ehr.clinical_notes TO ehr_read;

-- UC 1 (Tahseen Arnan). The catalog of what can be ordered: not PHI, grants only, seeded by its migration and read by
-- both apps. Nothing at runtime changes it.
GRANT SELECT ON ehr.order_catalog_items TO ehr_app, ehr_read;

-- Orders. PHI: row-level security is enabled and forced, and every write is recorded by trigger, in the policy files
-- that follow. A signed order is never edited and never deleted: there is no table-wide UPDATE and no DELETE. The one
-- change is discontinuing an order, so UPDATE is granted on just the three columns that writes: the status, the reason,
-- and the order that replaced it. Who and when are set by the trigger that also holds the update to that one change
-- (60-orders.sql). The signing trigger locks the patient's row, which takes the UPDATE ehr_app has on patients.
GRANT SELECT, INSERT ON ehr.orders TO ehr_app;
GRANT UPDATE (status, discontinue_reason, replaced_by_order_id) ON ehr.orders TO ehr_app;
GRANT SELECT ON ehr.orders TO ehr_read;

-- The staff inbox: what needs a staff member's action. PHI: row-level security and the write-audit trigger follow.
-- Read by its recipient. The one change is marking an item done: UPDATE on that column alone, and a trigger sets the
-- time and refuses anything else (70-inbox.sql). No INSERT yet: each kind of item gets its grant, or its trigger, with
-- the feature that creates it. Never DELETE.
GRANT SELECT ON ehr.inbox_items TO ehr_app, ehr_read;
GRANT UPDATE (done_at) ON ehr.inbox_items TO ehr_app;

-- UC 13: private, auditable proposals and decisions; replica reads use identical forced ownership RLS.
GRANT SELECT, INSERT, UPDATE ON ehr.ai_note_reviews, ehr.ai_field_proposals TO ehr_app;
GRANT SELECT ON ehr.ai_note_reviews, ehr.ai_field_proposals TO ehr_read;

COMMIT;
