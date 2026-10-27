-- Row-level security: the second layer of authorization (AGENTS.md). Applied as ehr_owner after migrations, on every
-- run of infra/migrate; safe to re-run. A missed check in the app must still return no unauthorized rows, so RLS is
-- enabled AND forced on every PHI table and on the audit log; forced, so even the owner is bound by the policies.
--
-- Who the current user is comes from app.user_id, which UserContextInterceptor (Ehr.Data) sets with
-- set_config(..., true) as the first statement of every transaction. With nobody signed in it is '' rather than
-- NULL, which is why current_user_id() goes through NULLIF. The functions are STABLE, so PostgreSQL evaluates them
-- once per query rather than once per row, and they run as the caller: the account tables are not under RLS, and
-- both runtime roles may read them.
--
-- The access model: a clinician sees and writes every patient in the clinic, and keeps a panel of their own
-- patients (patient_assignments), which is what they see first; a patient sees their own record; an admin manages
-- accounts and reads the audit log, but has no access to patient data. Opening the chart of a patient off one's
-- panel asks for a reason, which the app records (Ehr.Domain.Patients.BreakGlass); the database does not gate it.

CREATE OR REPLACE FUNCTION ehr.current_user_id() RETURNS uuid
    LANGUAGE sql STABLE
    RETURN NULLIF(current_setting('app.user_id', true), '')::uuid;

-- The roles of the signed-in staff account, or NULL when the user is not an active staff account. A locked-out
-- account (how an account is disabled) loses its access here too, not only at sign-in.
CREATE OR REPLACE FUNCTION ehr.current_staff_roles() RETURNS text[]
    LANGUAGE sql STABLE
    RETURN (
        SELECT roles FROM ehr.staff_users
        WHERE id = ehr.current_user_id() AND (lockout_end IS NULL OR lockout_end <= now())
    );

CREATE OR REPLACE FUNCTION ehr.is_clinician() RETURNS boolean
    LANGUAGE sql STABLE
    RETURN coalesce(ehr.current_staff_roles() @> ARRAY['Clinician'], false);

CREATE OR REPLACE FUNCTION ehr.is_admin() RETURNS boolean
    LANGUAGE sql STABLE
    RETURN coalesce(ehr.current_staff_roles() @> ARRAY['Admin'], false);

-- The patient the signed-in patient account belongs to, or NULL when the user is not an active patient account.
CREATE OR REPLACE FUNCTION ehr.current_patient_id() RETURNS uuid
    LANGUAGE sql STABLE
    RETURN (
        SELECT patient_id FROM ehr.patient_users
        WHERE id = ehr.current_user_id() AND (lockout_end IS NULL OR lockout_end <= now())
    );

-- Patients (PHI). Clinicians read and write; a patient reads their own row. Nobody deletes (10-grants.sql).
ALTER TABLE ehr.patients ENABLE ROW LEVEL SECURITY;
ALTER TABLE ehr.patients FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS patients_clinician_select ON ehr.patients;
CREATE POLICY patients_clinician_select ON ehr.patients FOR SELECT USING (ehr.is_clinician());

DROP POLICY IF EXISTS patients_clinician_insert ON ehr.patients;
CREATE POLICY patients_clinician_insert ON ehr.patients FOR INSERT WITH CHECK (ehr.is_clinician());

DROP POLICY IF EXISTS patients_clinician_update ON ehr.patients;
CREATE POLICY patients_clinician_update ON ehr.patients FOR UPDATE
    USING (ehr.is_clinician()) WITH CHECK (ehr.is_clinician());

DROP POLICY IF EXISTS patients_own_select ON ehr.patients;
CREATE POLICY patients_own_select ON ehr.patients FOR SELECT USING (id = ehr.current_patient_id());

-- Patient assignments (PHI). Clinicians read every panel; a clinician puts patients on their own panel and ends their
-- own assignments, nobody else's; a patient reads who their clinicians are. Nobody deletes (10-grants.sql).
ALTER TABLE ehr.patient_assignments ENABLE ROW LEVEL SECURITY;
ALTER TABLE ehr.patient_assignments FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS patient_assignments_clinician_select ON ehr.patient_assignments;
CREATE POLICY patient_assignments_clinician_select ON ehr.patient_assignments FOR SELECT USING (ehr.is_clinician());

DROP POLICY IF EXISTS patient_assignments_own_insert ON ehr.patient_assignments;
CREATE POLICY patient_assignments_own_insert ON ehr.patient_assignments FOR INSERT
    WITH CHECK (ehr.is_clinician() AND clinician_id = ehr.current_user_id());

DROP POLICY IF EXISTS patient_assignments_own_update ON ehr.patient_assignments;
CREATE POLICY patient_assignments_own_update ON ehr.patient_assignments FOR UPDATE
    USING (ehr.is_clinician() AND clinician_id = ehr.current_user_id())
    WITH CHECK (ehr.is_clinician() AND clinician_id = ehr.current_user_id());

DROP POLICY IF EXISTS patient_assignments_patient_select ON ehr.patient_assignments;
CREATE POLICY patient_assignments_patient_select ON ehr.patient_assignments FOR SELECT
    USING (patient_id = ehr.current_patient_id());

-- Audit events (restricted). Admins read. Anyone the grants let insert may insert: the app role for read and
-- sign-in events, and the write-audit trigger (40-audit.sql), which runs as the table's owner and is bound by this
-- policy too because RLS is forced. Nothing updates or deletes (10-grants.sql).
ALTER TABLE ehr.audit_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE ehr.audit_events FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS audit_events_admin_select ON ehr.audit_events;
CREATE POLICY audit_events_admin_select ON ehr.audit_events FOR SELECT USING (ehr.is_admin());

DROP POLICY IF EXISTS audit_events_insert ON ehr.audit_events;
CREATE POLICY audit_events_insert ON ehr.audit_events FOR INSERT WITH CHECK (true);
