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
-- for ehr_app, and never DELETE. A visit is created when a patient is checked in for an appointment (INSERT, further
-- down); nothing updates or deletes one.
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
-- classes its ingredients belong to and which of those cross-react, known interactions between ingredients, and the
-- visit types a note is written for with what to cover in each section (step 14). Read by both apps; nothing at
-- runtime changes any of it. Its rows are db/reference's SQL.
GRANT SELECT ON ehr.observation_codes, ehr.order_result_codes, ehr.questionnaires, ehr.drug_ingredients, ehr.drug_classes,
    ehr.drug_class_cross_reactions, ehr.drug_interactions, ehr.vital_reference_ranges, ehr.note_templates TO ehr_app, ehr_read;

-- Test results and vital signs (observations). PHI, like the chart: row-level security and the write-audit trigger
-- follow. A clinician files the results of a lab order, or records vital signs: INSERT, which a trigger holds to a
-- final result of one of the order's tests, or to a vital sign with no order, each with the name, unit and reference
-- range the database knows (75-results.sql). No DELETE, and no UPDATE but the one below: neither is changed once filed. The portal reads them
-- on either server.
GRANT SELECT, INSERT ON ehr.observations TO ehr_app;
-- Marking a vital sign "entered in error" (step 9b): these two columns, and a trigger holds the update to exactly that
-- (75-results.sql). No other column of an observation is ever updated by the apps.
GRANT UPDATE (status, entered_in_error_reason) ON ehr.observations TO ehr_app;
GRANT SELECT ON ehr.observations TO ehr_read;

-- Acknowledgments of an order's results. PHI. Made once: INSERT, never UPDATE or DELETE.
GRANT SELECT, INSERT ON ehr.result_acknowledgments TO ehr_app;
GRANT SELECT ON ehr.result_acknowledgments TO ehr_read;

-- Portal enrollment codes and invitations to create a proxy-only account (step 6c). Internal, and with no grant at
-- all to the runtime roles: the pages that use them run before anyone is signed in, so anything granted here would be
-- readable by an app session with nobody signed in, every row of it. They are reached only through the functions in
-- 45-codes.sql, which run as the tables' owner and answer for one code at a time.

-- The lab's key, as its hash (step 12). Internal, and with no grant at all to the runtime roles: a lab is let in by the
-- functions in 76-lab.sql, which find the hash they are given here as the table's owner, so a session that can't name
-- the hash can't use them, and none can read it.

-- Audit events. Restricted: row-level security is enabled and forced, in the policy files that follow; no trigger,
-- since nothing audits the audit. Append-only: no UPDATE and no DELETE for anyone at runtime. The write-audit
-- trigger inserts as the table's owner; the app role inserts read and sign-in events.
GRANT INSERT ON ehr.audit_events TO ehr_app;
-- Every column but details, which holds the values a write changed: admins get those through
-- ehr.shown_audit_details (40-audit.sql), which never gives a sensitive item's.
GRANT SELECT (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source, reason, sensitive, part) ON ehr.audit_events TO ehr_app, ehr_read;

GRANT SELECT, INSERT, UPDATE ON ehr.clinical_drafts TO ehr_app;

-- Final clinical records are append-only, but for their sensitivity (below). Cloud reads remain subject to the same
-- forced RLS.
GRANT SELECT ON ehr.clinical_drafts TO ehr_read;
GRANT SELECT, INSERT ON ehr.clinical_notes TO ehr_app;
GRANT SELECT ON ehr.clinical_notes TO ehr_read;
-- Marking a signed note or addendum sensitive, or unmarking it, with a reason (step 10b): these two columns, and a
-- trigger holds the update to exactly that and sets who and when (50-clinical-notes.sql). No other column of either
-- is ever updated.
GRANT UPDATE (sensitive, sensitive_reason) ON ehr.clinical_notes TO ehr_app;

-- Addenda to signed notes (step 10). PHI, append-only like the notes: a clinician signs one (INSERT), nothing deletes
-- one, and its one update is its sensitivity (step 10b). Row-level security, the signing trigger and the write-audit
-- trigger follow.
GRANT SELECT, INSERT ON ehr.note_addenda TO ehr_app;
GRANT UPDATE (sensitive, sensitive_reason) ON ehr.note_addenda TO ehr_app;
GRANT SELECT ON ehr.note_addenda TO ehr_read;

-- Visit summaries (step 13a): what a clinician writes for the patient from a signed note. PHI: row-level security, the
-- write-audit trigger and the trigger that holds every change follow (52-visit-summaries.sql). A clinician releases
-- one (INSERT). The UPDATEs the apps make are withdrawing it, with a reason, and marking it sensitive or unmarking it,
-- with a reason, as for an addendum, on those columns alone: who and when, and whether a mark is inherited, are the
-- database's. Never DELETE. The portal reads them on either server.
GRANT SELECT, INSERT ON ehr.visit_summaries TO ehr_app;
GRANT UPDATE (withdrawn_reason, sensitive, sensitive_reason) ON ehr.visit_summaries TO ehr_app;
GRANT SELECT ON ehr.visit_summaries TO ehr_read;

-- Refill requests (step 13d): a patient's ask to renew a medication. PHI: row-level security, the write-audit trigger
-- and the trigger that holds every change follow (77-refill-requests.sql). A patient or their proxy asks (INSERT). The
-- one UPDATE the apps make is the answer, on these two columns alone: the order that approves it, or the message that
-- declines it; its state, who and when, what was ordered and its sensitivity are the database's. Never DELETE.
GRANT SELECT, INSERT ON ehr.refill_requests TO ehr_app;
GRANT UPDATE (order_id, decline_message) ON ehr.refill_requests TO ehr_app;
GRANT SELECT ON ehr.refill_requests TO ehr_read;

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

-- Proxy grants (FR-19). PHI: row-level security and the write-audit trigger follow. The patient, or a clinician with
-- them on their panel, gives one: INSERT. The only UPDATEs are revoking it and, for the patient, changing whether
-- sensitive items are included, on those columns alone; a trigger holds them to that (95-proxy-access.sql). Never
-- DELETE: a grant that ended stays as the record of who could see what, and when.
GRANT SELECT, INSERT ON ehr.proxy_grants TO ehr_app;
GRANT UPDATE (include_sensitive, revoked_at, revoked_by) ON ehr.proxy_grants TO ehr_app;
GRANT SELECT ON ehr.proxy_grants TO ehr_read;

-- Secure messages (FR-18). PHI: row-level security and the write-audit trigger follow. A patient and their care
-- team start threads and send messages: INSERT. Nothing edits a thread or a message: the one UPDATE is recording when
-- the other side first read a message, on that column alone, and a trigger holds it to that (90-messages.sql). Never
-- DELETE.
GRANT SELECT, INSERT ON ehr.message_threads, ehr.messages TO ehr_app;
GRANT UPDATE (read_at) ON ehr.messages TO ehr_app;
GRANT SELECT ON ehr.message_threads, ehr.messages TO ehr_read;

-- Appointments (FR-17). PHI: row-level security and the write-audit trigger follow. Clinicians book and change them:
-- INSERT, and UPDATE of the columns a reschedule, a cancellation, a check-in or a no-show writes, which a trigger
-- holds to those moves (80-appointments.sql). Never DELETE. Checking a patient in creates their visit, so ehr_app
-- may now INSERT encounters, under a policy of its own (30-rls.sql); nothing updates or deletes a visit.
GRANT SELECT, INSERT ON ehr.appointments TO ehr_app;
GRANT UPDATE (starts_at, minutes, reason, status, cancel_reason, encounter_id) ON ehr.appointments TO ehr_app;
GRANT SELECT ON ehr.appointments TO ehr_read;
GRANT INSERT ON ehr.encounters TO ehr_app;

-- Pre-visit forms (step 13e): a clinician's request that a patient fill in a questionnaire before an appointment, and
-- the answers. PHI, and sensitive items always: row-level security and the write-audit trigger follow. A clinician
-- asks and a portal session answers: INSERT, which a trigger holds to what each may do and fills in itself
-- (85-pre-visit-forms.sql). Never UPDATE or DELETE: a form is a record.
GRANT SELECT, INSERT ON ehr.questionnaire_requests, ehr.questionnaire_responses TO ehr_app;
GRANT SELECT ON ehr.questionnaire_requests, ehr.questionnaire_responses TO ehr_read;

-- The staff inbox: what needs a staff member's action. PHI: row-level security and the write-audit trigger follow.
-- Read by its recipient. The one change is marking an item done: UPDATE on that column alone, and a trigger sets the
-- time and refuses anything else (70-inbox.sql). No INSERT yet: each kind of item gets its grant, or its trigger, with
-- the feature that creates it. Never DELETE.
GRANT SELECT ON ehr.inbox_items TO ehr_app, ehr_read;
GRANT UPDATE (done_at) ON ehr.inbox_items TO ehr_app;

-- UC 13: private, auditable proposals and decisions; replica reads use identical forced ownership RLS.
GRANT SELECT, INSERT, UPDATE ON ehr.ai_note_reviews, ehr.ai_field_proposals TO ehr_app;
GRANT SELECT ON ehr.ai_note_reviews, ehr.ai_field_proposals TO ehr_read;

-- The expansion plan's step 15: the orders and allergies to add a dictation proposes, staged with their review and
-- decided once. The decision is a column grant on its three columns; who and when are the database's, and what each
-- decision needs is 55-ai-review.sql's. Never DELETE.
GRANT SELECT, INSERT ON ehr.ai_chart_proposals TO ehr_app;
GRANT UPDATE (state, order_id, allergy_id) ON ehr.ai_chart_proposals TO ehr_app;
GRANT SELECT ON ehr.ai_chart_proposals TO ehr_read;

-- Ambient listening (step 16): a clinician's consent to record a visit, and a conversation's transcript while its review
-- is open. PHI: row-level security and the write-audit trigger follow (56-ambient-listening.sql has the rules). A consent
-- is a record: INSERT, never changed or deleted. A transcript is written as its review is staged, never changed, and
-- deleted as it is finished: DELETE, which the guard allows only then (the purge after 7 days is its owner's function).
-- The cloud app may see that a transcript exists, to know a review's proposals are to be decided at the clinic, but
-- never read one: no SELECT on the transcript column.
GRANT SELECT, INSERT ON ehr.ambient_consents TO ehr_app;
GRANT SELECT ON ehr.ambient_consents TO ehr_read;
GRANT SELECT, INSERT, DELETE ON ehr.ambient_transcripts TO ehr_app;
GRANT SELECT (id, patient_id, sensitive, created_at) ON ehr.ambient_transcripts TO ehr_read;

COMMIT;
