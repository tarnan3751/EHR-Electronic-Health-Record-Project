-- Row-level security: the second layer of authorization (AGENTS.md). Applied as ehr_owner after migrations, on every
-- run of infra/migrate; safe to re-run. A missed check in the app must still return no unauthorized rows, so RLS is
-- enabled AND forced on every PHI table and on the audit log; forced, so even the owner is bound by the policies.
--
-- Who the current user is comes from app.user_id, which UserContextInterceptor (Ehr.Data) sets with
-- set_config(..., true) as the first statement of every transaction. With nobody signed in it is '' rather than
-- NULL, which is why current_user_id() goes through NULLIF. The functions run as the caller: the account tables are
-- not under RLS, and both runtime roles may read them.
--
-- Every policy calls them as scalar subqueries, (SELECT ehr.is_clinician()) rather than ehr.is_clinician(), so that
-- PostgreSQL runs each once per statement, as an InitPlan. Called directly, they ran once for every row the statement
-- looked at, since PostgreSQL can't inline a function whose body is a subquery: two lookups in the account tables per
-- row, 200 ms for a search across 50,000 patients. It is the same answer either way: the functions are STABLE, take no
-- arguments, and read only app.user_id and the account tables, never the row, so within a statement every call returns
-- the same value. Ehr.Security.Tests fails if a policy calls one directly.
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
-- Whatever record a portal session is looking at, this is the account's own: who may give or take away access to a
-- record is decided on it.
CREATE OR REPLACE FUNCTION ehr.own_patient_id() RETURNS uuid
    LANGUAGE sql STABLE
    RETURN (
        SELECT patient_id FROM ehr.patient_users
        WHERE id = ehr.current_user_id() AND (lockout_end IS NULL OR lockout_end <= now())
    );

-- Proxy access (FR-19). A portal session may act for another patient under a proxy grant (ehr.proxy_grants), which
-- the app names in app.acting_grant beside app.user_id (UserContextInterceptor). The app only names it: these
-- functions decide whether it counts.

-- The grant the session says it acts under, or NULL when it is looking at its own record.
CREATE OR REPLACE FUNCTION ehr.acting_grant_id() RETURNS uuid
    LANGUAGE sql STABLE
    RETURN NULLIF(current_setting('app.acting_grant', true), '')::uuid;

-- The signed-in patient account's confirmed email, as ASP.NET Core Identity normalizes it, or NULL: a grant is
-- addressed to an email, and only an account that has proved it owns the address can use it. Only a plain ASCII
-- address counts: Identity's normalizing folds some look-alike letters together (a long s becomes S), so an account
-- at a look-alike address would otherwise match a grant written for someone else. Grants are ASCII too
-- (95-proxy-access.sql).
CREATE OR REPLACE FUNCTION ehr.current_account_email() RETURNS text
    LANGUAGE sql STABLE
    RETURN (
        SELECT normalized_email FROM ehr.patient_users
        WHERE id = ehr.current_user_id() AND email_confirmed AND (lockout_end IS NULL OR lockout_end <= now())
          AND email ~ '^[\x21-\x7E]+$'
    );

-- The patient whose record the session is looking at: the portal's subject, which every *_own_* policy keys on.
-- Without a grant named, the account's own patient. With one, that grant's patient while the grant is addressed to
-- this account, not revoked and not expired; otherwise nobody, never the account's own, so a grant that has ended
-- shows nothing rather than a different person's record under the first one's name.
CREATE OR REPLACE FUNCTION ehr.current_patient_id() RETURNS uuid
    LANGUAGE sql STABLE
    RETURN CASE
        WHEN ehr.acting_grant_id() IS NULL THEN ehr.own_patient_id()
        ELSE (
            SELECT g.patient_id FROM ehr.proxy_grants g
            WHERE g.id = ehr.acting_grant_id() AND g.revoked_at IS NULL AND g.expires_at > now()
              AND g.proxy_email = ehr.current_account_email()
        )
    END;

-- Whether the portal's subject may see items marked sensitive: always their own; a proxy only when the patient
-- turned that on for the grant.
CREATE OR REPLACE FUNCTION ehr.portal_sees_sensitive() RETURNS boolean
    LANGUAGE sql STABLE
    RETURN ehr.acting_grant_id() IS NULL OR EXISTS (
        SELECT 1 FROM ehr.proxy_grants g
        WHERE g.id = ehr.acting_grant_id() AND g.revoked_at IS NULL AND g.expires_at > now()
          AND g.proxy_email = ehr.current_account_email() AND g.include_sensitive
    );

-- Whether the portal's subject may read and send messages: always their own; a proxy only when the grant's scope
-- includes them.
CREATE OR REPLACE FUNCTION ehr.portal_may_message() RETURNS boolean
    LANGUAGE sql STABLE
    RETURN ehr.acting_grant_id() IS NULL OR EXISTS (
        SELECT 1 FROM ehr.proxy_grants g
        WHERE g.id = ehr.acting_grant_id() AND g.revoked_at IS NULL AND g.expires_at > now()
          AND g.proxy_email = ehr.current_account_email() AND g.scope = 'record-and-messages'
    );

-- The records the signed-in account holds a grant for, in force now, with each patient's name and nothing else of
-- theirs: the portal's "records you can view" list, before one is chosen. Row-level security gives a portal session
-- one patient's row at a time, its own or the one it is acting for, so the names come from here: as the owner, who
-- reads the two tables through policies of its own below, and only ever for the caller's own address.
DROP FUNCTION IF EXISTS ehr.proxied_patient_ids();
CREATE OR REPLACE FUNCTION ehr.proxied_records()
    RETURNS TABLE (grant_id uuid, patient_id uuid, given_name text, family_name text, scope text, include_sensitive boolean, expires_at timestamptz)
    LANGUAGE sql
    STABLE
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
    SELECT g.id, g.patient_id, p.given_name::text, p.family_name::text, g.scope::text, g.include_sensitive, g.expires_at
    FROM ehr.proxy_grants g
    JOIN ehr.patients p ON p.id = g.patient_id
    WHERE g.revoked_at IS NULL AND g.expires_at > now() AND g.proxy_email = (SELECT ehr.current_account_email())
$$;
REVOKE ALL ON FUNCTION ehr.proxied_records() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ehr.proxied_records() TO ehr_app, ehr_read;

-- Proxy grants (PHI: they say who has a record at this clinic and who helps them). The patient sees and manages
-- their own, as themselves and never while acting for someone else; a clinician sees them all, as they see charts,
-- and records or revokes one for a patient on their active panel; the proxy sees the ones addressed to them while
-- they are in force. A trigger sets who and when and holds an update to revoking it or, for the patient alone,
-- changing whether sensitive items are included (95-proxy-access.sql). Nothing deletes (10-grants.sql).
ALTER TABLE ehr.proxy_grants ENABLE ROW LEVEL SECURITY;
ALTER TABLE ehr.proxy_grants FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS proxy_grants_clinician_select ON ehr.proxy_grants;
CREATE POLICY proxy_grants_clinician_select ON ehr.proxy_grants FOR SELECT USING ((SELECT ehr.is_clinician()));
DROP POLICY IF EXISTS proxy_grants_own_select ON ehr.proxy_grants;
CREATE POLICY proxy_grants_own_select ON ehr.proxy_grants FOR SELECT
    USING (patient_id = (SELECT ehr.own_patient_id()));
DROP POLICY IF EXISTS proxy_grants_proxy_select ON ehr.proxy_grants;
CREATE POLICY proxy_grants_proxy_select ON ehr.proxy_grants FOR SELECT
    USING (proxy_email = (SELECT ehr.current_account_email()) AND revoked_at IS NULL AND expires_at > now());
-- For the tables' owner alone, which ehr.proxied_records runs as: no runtime role is or can become it.
DROP POLICY IF EXISTS proxy_grants_owner_select ON ehr.proxy_grants;
CREATE POLICY proxy_grants_owner_select ON ehr.proxy_grants FOR SELECT TO ehr_owner USING (true);
DROP POLICY IF EXISTS proxy_grants_own_insert ON ehr.proxy_grants;
CREATE POLICY proxy_grants_own_insert ON ehr.proxy_grants FOR INSERT
    WITH CHECK (patient_id = (SELECT ehr.own_patient_id()) AND (SELECT ehr.acting_grant_id()) IS NULL);
DROP POLICY IF EXISTS proxy_grants_panel_insert ON ehr.proxy_grants;
CREATE POLICY proxy_grants_panel_insert ON ehr.proxy_grants FOR INSERT
    WITH CHECK (
        (SELECT ehr.is_clinician())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = proxy_grants.patient_id AND a.ended_at IS NULL
        )
    );
DROP POLICY IF EXISTS proxy_grants_own_update ON ehr.proxy_grants;
CREATE POLICY proxy_grants_own_update ON ehr.proxy_grants FOR UPDATE
    USING (patient_id = (SELECT ehr.own_patient_id()) AND (SELECT ehr.acting_grant_id()) IS NULL)
    WITH CHECK (patient_id = (SELECT ehr.own_patient_id()) AND (SELECT ehr.acting_grant_id()) IS NULL);
DROP POLICY IF EXISTS proxy_grants_panel_update ON ehr.proxy_grants;
CREATE POLICY proxy_grants_panel_update ON ehr.proxy_grants FOR UPDATE
    USING (
        (SELECT ehr.is_clinician())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = proxy_grants.patient_id AND a.ended_at IS NULL
        )
    )
    WITH CHECK (
        (SELECT ehr.is_clinician())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = proxy_grants.patient_id AND a.ended_at IS NULL
        )
    );

-- Patients (PHI). Clinicians read and write; a patient reads their own row. Nobody deletes (10-grants.sql).
ALTER TABLE ehr.patients ENABLE ROW LEVEL SECURITY;
ALTER TABLE ehr.patients FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS patients_clinician_select ON ehr.patients;
CREATE POLICY patients_clinician_select ON ehr.patients FOR SELECT USING ((SELECT ehr.is_clinician()));

DROP POLICY IF EXISTS patients_clinician_insert ON ehr.patients;
CREATE POLICY patients_clinician_insert ON ehr.patients FOR INSERT WITH CHECK ((SELECT ehr.is_clinician()));

DROP POLICY IF EXISTS patients_clinician_update ON ehr.patients;
CREATE POLICY patients_clinician_update ON ehr.patients FOR UPDATE
    USING ((SELECT ehr.is_clinician())) WITH CHECK ((SELECT ehr.is_clinician()));

DROP POLICY IF EXISTS patients_own_select ON ehr.patients;
CREATE POLICY patients_own_select ON ehr.patients FOR SELECT USING (id = (SELECT ehr.current_patient_id()));


-- A portal session reads one patient's row: its own, or the one it is acting for (ehr.current_patient_id). The
-- names of the records a proxy may choose come from ehr.proxied_records, which reads this table as its owner.
DROP POLICY IF EXISTS patients_proxy_select ON ehr.patients;
DROP POLICY IF EXISTS patients_owner_select ON ehr.patients;
CREATE POLICY patients_owner_select ON ehr.patients FOR SELECT TO ehr_owner USING (true);

-- Patient assignments (PHI). Clinicians read every panel; a clinician puts patients on their own panel and ends their
-- own assignments, nobody else's; a patient reads who their clinicians are. Nobody deletes (10-grants.sql).
ALTER TABLE ehr.patient_assignments ENABLE ROW LEVEL SECURITY;
ALTER TABLE ehr.patient_assignments FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS patient_assignments_clinician_select ON ehr.patient_assignments;
CREATE POLICY patient_assignments_clinician_select ON ehr.patient_assignments FOR SELECT USING ((SELECT ehr.is_clinician()));

DROP POLICY IF EXISTS patient_assignments_own_insert ON ehr.patient_assignments;
CREATE POLICY patient_assignments_own_insert ON ehr.patient_assignments FOR INSERT
    WITH CHECK ((SELECT ehr.is_clinician()) AND clinician_id = (SELECT ehr.current_user_id()));

DROP POLICY IF EXISTS patient_assignments_own_update ON ehr.patient_assignments;
CREATE POLICY patient_assignments_own_update ON ehr.patient_assignments FOR UPDATE
    USING ((SELECT ehr.is_clinician()) AND clinician_id = (SELECT ehr.current_user_id()))
    WITH CHECK ((SELECT ehr.is_clinician()) AND clinician_id = (SELECT ehr.current_user_id()));

DROP POLICY IF EXISTS patient_assignments_patient_select ON ehr.patient_assignments;
CREATE POLICY patient_assignments_patient_select ON ehr.patient_assignments FOR SELECT
    USING (patient_id = (SELECT ehr.current_patient_id()));

-- Emergency access (PHI). A clinician records and reads their own; admins review it in the audit log, where the
-- break_glass event carries the same reason. Nothing updates or deletes (10-grants.sql).
ALTER TABLE ehr.break_glass_accesses ENABLE ROW LEVEL SECURITY;
ALTER TABLE ehr.break_glass_accesses FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS break_glass_accesses_own_select ON ehr.break_glass_accesses;
CREATE POLICY break_glass_accesses_own_select ON ehr.break_glass_accesses FOR SELECT
    USING ((SELECT ehr.is_clinician()) AND clinician_id = (SELECT ehr.current_user_id()));

DROP POLICY IF EXISTS break_glass_accesses_own_insert ON ehr.break_glass_accesses;
CREATE POLICY break_glass_accesses_own_insert ON ehr.break_glass_accesses FOR INSERT
    WITH CHECK ((SELECT ehr.is_clinician()) AND clinician_id = (SELECT ehr.current_user_id()));

-- The rest of the chart (PHI): allergies, medications, diagnoses and visits, each row one patient's. Clinicians read
-- every patient's; a patient reads their own. A visit is inserted when a patient is checked in for an appointment
-- (encounters_panel_insert, with the appointments' policies below); nothing updates or deletes one (10-grants.sql).
-- Allergies, medications and diagnoses are added and changed by a clinician for a patient on their active panel, the
-- rule for signing an order; an update can't move an entry to a patient off the panel either (WITH CHECK). Nothing
-- deletes one, so there is no policy for it.
ALTER TABLE ehr.allergy_intolerances ENABLE ROW LEVEL SECURITY;
ALTER TABLE ehr.allergy_intolerances FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS allergy_intolerances_clinician_select ON ehr.allergy_intolerances;
CREATE POLICY allergy_intolerances_clinician_select ON ehr.allergy_intolerances FOR SELECT USING ((SELECT ehr.is_clinician()));

DROP POLICY IF EXISTS allergy_intolerances_own_select ON ehr.allergy_intolerances;
CREATE POLICY allergy_intolerances_own_select ON ehr.allergy_intolerances FOR SELECT
    USING (patient_id = (SELECT ehr.current_patient_id()));

DROP POLICY IF EXISTS allergy_intolerances_panel_insert ON ehr.allergy_intolerances;
CREATE POLICY allergy_intolerances_panel_insert ON ehr.allergy_intolerances FOR INSERT
    WITH CHECK (
        (SELECT ehr.is_clinician())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = allergy_intolerances.patient_id AND a.ended_at IS NULL
        )
    );
DROP POLICY IF EXISTS allergy_intolerances_panel_update ON ehr.allergy_intolerances;
CREATE POLICY allergy_intolerances_panel_update ON ehr.allergy_intolerances FOR UPDATE
    USING (
        (SELECT ehr.is_clinician())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = allergy_intolerances.patient_id AND a.ended_at IS NULL
        )
    )
    WITH CHECK (
        (SELECT ehr.is_clinician())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = allergy_intolerances.patient_id AND a.ended_at IS NULL
        )
    );

ALTER TABLE ehr.medication_statements ENABLE ROW LEVEL SECURITY;
ALTER TABLE ehr.medication_statements FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS medication_statements_clinician_select ON ehr.medication_statements;
CREATE POLICY medication_statements_clinician_select ON ehr.medication_statements FOR SELECT USING ((SELECT ehr.is_clinician()));

DROP POLICY IF EXISTS medication_statements_own_select ON ehr.medication_statements;
CREATE POLICY medication_statements_own_select ON ehr.medication_statements FOR SELECT
    USING (patient_id = (SELECT ehr.current_patient_id()) AND (NOT sensitive OR (SELECT ehr.portal_sees_sensitive())));

DROP POLICY IF EXISTS medication_statements_panel_insert ON ehr.medication_statements;
CREATE POLICY medication_statements_panel_insert ON ehr.medication_statements FOR INSERT
    WITH CHECK (
        (SELECT ehr.is_clinician())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = medication_statements.patient_id AND a.ended_at IS NULL
        )
    );
DROP POLICY IF EXISTS medication_statements_panel_update ON ehr.medication_statements;
CREATE POLICY medication_statements_panel_update ON ehr.medication_statements FOR UPDATE
    USING (
        (SELECT ehr.is_clinician())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = medication_statements.patient_id AND a.ended_at IS NULL
        )
    )
    WITH CHECK (
        (SELECT ehr.is_clinician())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = medication_statements.patient_id AND a.ended_at IS NULL
        )
    );

ALTER TABLE ehr.conditions ENABLE ROW LEVEL SECURITY;
ALTER TABLE ehr.conditions FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS conditions_clinician_select ON ehr.conditions;
CREATE POLICY conditions_clinician_select ON ehr.conditions FOR SELECT USING ((SELECT ehr.is_clinician()));

DROP POLICY IF EXISTS conditions_own_select ON ehr.conditions;
CREATE POLICY conditions_own_select ON ehr.conditions FOR SELECT
    USING (patient_id = (SELECT ehr.current_patient_id()) AND (NOT sensitive OR (SELECT ehr.portal_sees_sensitive())));

DROP POLICY IF EXISTS conditions_panel_insert ON ehr.conditions;
CREATE POLICY conditions_panel_insert ON ehr.conditions FOR INSERT
    WITH CHECK (
        (SELECT ehr.is_clinician())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = conditions.patient_id AND a.ended_at IS NULL
        )
    );
DROP POLICY IF EXISTS conditions_panel_update ON ehr.conditions;
CREATE POLICY conditions_panel_update ON ehr.conditions FOR UPDATE
    USING (
        (SELECT ehr.is_clinician())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = conditions.patient_id AND a.ended_at IS NULL
        )
    )
    WITH CHECK (
        (SELECT ehr.is_clinician())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = conditions.patient_id AND a.ended_at IS NULL
        )
    );

-- The history of changes to allergies, medications and diagnoses (PHI). Clinicians read every patient's, as they read
-- the lists themselves. Patients don't: the portal shows the lists as they are. No write policies and no write grants:
-- its rows come from the database's own trigger (65-chart-lists.sql).
ALTER TABLE ehr.chart_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE ehr.chart_history FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS chart_history_clinician_select ON ehr.chart_history;
CREATE POLICY chart_history_clinician_select ON ehr.chart_history FOR SELECT USING ((SELECT ehr.is_clinician()));
-- Forced row-level security binds the table's owner too, and the history trigger writes as the owner: this lets it,
-- and only it. The runtime roles have no INSERT grant, and the policy isn't theirs either.
DROP POLICY IF EXISTS chart_history_owner_insert ON ehr.chart_history;
CREATE POLICY chart_history_owner_insert ON ehr.chart_history FOR INSERT TO ehr_owner WITH CHECK (true);

ALTER TABLE ehr.encounters ENABLE ROW LEVEL SECURITY;
ALTER TABLE ehr.encounters FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS encounters_clinician_select ON ehr.encounters;
CREATE POLICY encounters_clinician_select ON ehr.encounters FOR SELECT USING ((SELECT ehr.is_clinician()));

DROP POLICY IF EXISTS encounters_own_select ON ehr.encounters;
CREATE POLICY encounters_own_select ON ehr.encounters FOR SELECT
    USING (patient_id = (SELECT ehr.current_patient_id()));

-- Test results (PHI). Clinicians read every patient's. A patient reads only their own, and only once the result is
-- released and finished (Observation.IssuedAt, ObservationStatus.Released): a result still being worked on, cancelled
-- or entered in error never reaches the portal, even through a bug in the app. A clinician files results for a patient
-- on their active panel; nothing updates or deletes one (10-grants.sql).
ALTER TABLE ehr.observations ENABLE ROW LEVEL SECURITY;
ALTER TABLE ehr.observations FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS observations_clinician_select ON ehr.observations;
CREATE POLICY observations_clinician_select ON ehr.observations FOR SELECT USING ((SELECT ehr.is_clinician()));

DROP POLICY IF EXISTS observations_panel_insert ON ehr.observations;
CREATE POLICY observations_panel_insert ON ehr.observations FOR INSERT
    WITH CHECK (
        (SELECT ehr.is_clinician())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = observations.patient_id AND a.ended_at IS NULL
        )
    );

-- Acknowledgments of results (PHI). Clinicians read them, as they read the orders; a clinician with the patient on
-- their active panel makes one, as themselves. Patients, admins and nobody see none. Never updated or deleted.
ALTER TABLE ehr.result_acknowledgments ENABLE ROW LEVEL SECURITY;
ALTER TABLE ehr.result_acknowledgments FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS result_acknowledgments_clinician_select ON ehr.result_acknowledgments;
CREATE POLICY result_acknowledgments_clinician_select ON ehr.result_acknowledgments FOR SELECT USING ((SELECT ehr.is_clinician()));
DROP POLICY IF EXISTS result_acknowledgments_panel_insert ON ehr.result_acknowledgments;
CREATE POLICY result_acknowledgments_panel_insert ON ehr.result_acknowledgments FOR INSERT
    WITH CHECK (
        (SELECT ehr.is_clinician())
        AND acknowledged_by = (SELECT ehr.current_user_id())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = result_acknowledgments.patient_id AND a.ended_at IS NULL
        )
    );

DROP POLICY IF EXISTS observations_own_released_select ON ehr.observations;
CREATE POLICY observations_own_released_select ON ehr.observations FOR SELECT
    USING (patient_id = (SELECT ehr.current_patient_id())
        AND issued_at IS NOT NULL
        AND status IN ('final', 'amended', 'corrected'));

-- Audit events (restricted). Admins read. Anyone the grants let insert may insert: the app role for read and
-- sign-in events, and the write-audit trigger (40-audit.sql), which runs as the table's owner and is bound by this
-- policy too because RLS is forced. Nothing deletes, and the one update is the trigger marking an event sensitive
-- (below, and 40-audit.sql); the runtime roles have neither grant (10-grants.sql).
ALTER TABLE ehr.audit_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE ehr.audit_events FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS audit_events_admin_select ON ehr.audit_events;
CREATE POLICY audit_events_admin_select ON ehr.audit_events FOR SELECT USING ((SELECT ehr.is_admin()));

DROP POLICY IF EXISTS audit_events_insert ON ehr.audit_events;
CREATE POLICY audit_events_insert ON ehr.audit_events FOR INSERT WITH CHECK (true);
-- The write-audit trigger, which runs as the table's owner, marks an item's earlier events sensitive when the item
-- becomes sensitive (40-audit.sql). Forced row-level security binds the owner too, so these two let it find those
-- events and mark them, and nothing else: the update can only set the mark, and a trigger refuses any other change.
-- They are the owner's alone; the runtime roles have no UPDATE grant.
DROP POLICY IF EXISTS audit_events_owner_select ON ehr.audit_events;
CREATE POLICY audit_events_owner_select ON ehr.audit_events FOR SELECT TO ehr_owner USING (true);
DROP POLICY IF EXISTS audit_events_owner_mark ON ehr.audit_events;
CREATE POLICY audit_events_owner_mark ON ehr.audit_events FOR UPDATE TO ehr_owner USING (NOT sensitive) WITH CHECK (sensitive);

-- Secure messages (PHI). Clinicians read every thread and message, as they read charts (the pages ask for the panel
-- or emergency access); a patient reads their own. A patient writes in their own threads; a clinician writes only for
-- a patient on their active panel. The one update is marking a message read, by the side that didn't send it: the
-- patient for a staff message, a panel clinician for the patient's. Nothing deletes (10-grants.sql).
ALTER TABLE ehr.message_threads ENABLE ROW LEVEL SECURITY;
ALTER TABLE ehr.message_threads FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS message_threads_clinician_select ON ehr.message_threads;
CREATE POLICY message_threads_clinician_select ON ehr.message_threads FOR SELECT USING ((SELECT ehr.is_clinician()));
DROP POLICY IF EXISTS message_threads_own_select ON ehr.message_threads;
CREATE POLICY message_threads_own_select ON ehr.message_threads FOR SELECT
    USING (patient_id = (SELECT ehr.current_patient_id()) AND (SELECT ehr.portal_may_message()));
DROP POLICY IF EXISTS message_threads_own_insert ON ehr.message_threads;
CREATE POLICY message_threads_own_insert ON ehr.message_threads FOR INSERT
    WITH CHECK (patient_id = (SELECT ehr.current_patient_id()) AND (SELECT ehr.portal_may_message()));
DROP POLICY IF EXISTS message_threads_panel_insert ON ehr.message_threads;
CREATE POLICY message_threads_panel_insert ON ehr.message_threads FOR INSERT
    WITH CHECK (
        (SELECT ehr.is_clinician())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = message_threads.patient_id AND a.ended_at IS NULL
        )
    );
ALTER TABLE ehr.messages ENABLE ROW LEVEL SECURITY;
ALTER TABLE ehr.messages FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS messages_clinician_select ON ehr.messages;
CREATE POLICY messages_clinician_select ON ehr.messages FOR SELECT USING ((SELECT ehr.is_clinician()));
DROP POLICY IF EXISTS messages_own_select ON ehr.messages;
CREATE POLICY messages_own_select ON ehr.messages FOR SELECT
    USING (patient_id = (SELECT ehr.current_patient_id()) AND (SELECT ehr.portal_may_message()));
DROP POLICY IF EXISTS messages_own_insert ON ehr.messages;
CREATE POLICY messages_own_insert ON ehr.messages FOR INSERT
    WITH CHECK (patient_id = (SELECT ehr.current_patient_id()) AND (SELECT ehr.portal_may_message())
        AND sender_kind = 'patient' AND sender_id = (SELECT ehr.current_user_id()));
DROP POLICY IF EXISTS messages_panel_insert ON ehr.messages;
CREATE POLICY messages_panel_insert ON ehr.messages FOR INSERT
    WITH CHECK (
        (SELECT ehr.is_clinician())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = messages.patient_id AND a.ended_at IS NULL
        )
        AND sender_kind = 'staff' AND sender_id = (SELECT ehr.current_user_id())
    );
DROP POLICY IF EXISTS messages_own_read ON ehr.messages;
CREATE POLICY messages_own_read ON ehr.messages FOR UPDATE
    -- The patient's own reading marks a staff message read, never a proxy's: the patient still sees it as new.
    USING (patient_id = (SELECT ehr.own_patient_id()) AND (SELECT ehr.acting_grant_id()) IS NULL AND sender_kind = 'staff')
    WITH CHECK (patient_id = (SELECT ehr.own_patient_id()) AND (SELECT ehr.acting_grant_id()) IS NULL AND sender_kind = 'staff');
DROP POLICY IF EXISTS messages_panel_read ON ehr.messages;
CREATE POLICY messages_panel_read ON ehr.messages FOR UPDATE
    USING (
        sender_kind = 'patient' AND (SELECT ehr.is_clinician())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = messages.patient_id AND a.ended_at IS NULL
        )
    )
    WITH CHECK (
        sender_kind = 'patient' AND (SELECT ehr.is_clinician())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = messages.patient_id AND a.ended_at IS NULL
        )
    );
-- A thread's latest-message time is kept by the messages' trigger, which runs as the tables' owner: forced row-level
-- security binds the owner too, so this lets it, and only it. The runtime roles have no UPDATE on threads.
DROP POLICY IF EXISTS message_threads_owner_select ON ehr.message_threads;
CREATE POLICY message_threads_owner_select ON ehr.message_threads FOR SELECT TO ehr_owner USING (true);
DROP POLICY IF EXISTS message_threads_owner_update ON ehr.message_threads;
CREATE POLICY message_threads_owner_update ON ehr.message_threads FOR UPDATE TO ehr_owner USING (true) WITH CHECK (true);
-- The same trigger puts a patient's message in each care-team clinician's inbox and marks those items done when one
-- of them replies: the owner's alone, again. The runtime roles still can't insert an inbox item.
DROP POLICY IF EXISTS inbox_items_owner_select ON ehr.inbox_items;
CREATE POLICY inbox_items_owner_select ON ehr.inbox_items FOR SELECT TO ehr_owner USING (true);
DROP POLICY IF EXISTS inbox_items_owner_insert ON ehr.inbox_items;
CREATE POLICY inbox_items_owner_insert ON ehr.inbox_items FOR INSERT TO ehr_owner WITH CHECK (true);
DROP POLICY IF EXISTS inbox_items_owner_update ON ehr.inbox_items;
CREATE POLICY inbox_items_owner_update ON ehr.inbox_items FOR UPDATE TO ehr_owner USING (done_at IS NULL) WITH CHECK (done_at IS NOT NULL);
DROP POLICY IF EXISTS patient_assignments_owner_select ON ehr.patient_assignments;
CREATE POLICY patient_assignments_owner_select ON ehr.patient_assignments FOR SELECT TO ehr_owner USING (true);

-- Appointments (PHI). Clinicians read every patient's, as they read charts; a patient reads their own. A clinician
-- books and changes them only for a patient on their active panel, the rule for orders and the chart's lists, and is
-- recorded as the one who booked. Nothing deletes one (10-grants.sql).
ALTER TABLE ehr.appointments ENABLE ROW LEVEL SECURITY;
ALTER TABLE ehr.appointments FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS appointments_clinician_select ON ehr.appointments;
CREATE POLICY appointments_clinician_select ON ehr.appointments FOR SELECT USING ((SELECT ehr.is_clinician()));
DROP POLICY IF EXISTS appointments_own_select ON ehr.appointments;
CREATE POLICY appointments_own_select ON ehr.appointments FOR SELECT
    USING (patient_id = (SELECT ehr.current_patient_id()));
DROP POLICY IF EXISTS appointments_panel_insert ON ehr.appointments;
CREATE POLICY appointments_panel_insert ON ehr.appointments FOR INSERT
    WITH CHECK (
        (SELECT ehr.is_clinician())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = appointments.patient_id AND a.ended_at IS NULL
        )
        AND booked_by_id = (SELECT ehr.current_user_id())
    );
DROP POLICY IF EXISTS appointments_panel_update ON ehr.appointments;
CREATE POLICY appointments_panel_update ON ehr.appointments FOR UPDATE
    USING (
        (SELECT ehr.is_clinician())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = appointments.patient_id AND a.ended_at IS NULL
        )
    )
    WITH CHECK (
        (SELECT ehr.is_clinician())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = appointments.patient_id AND a.ended_at IS NULL
        )
    );

-- A visit is created when a clinician checks a patient in for an appointment: for a patient on their active panel.
-- Nothing else at runtime writes visits, and nothing updates or deletes one.
DROP POLICY IF EXISTS encounters_panel_insert ON ehr.encounters;
CREATE POLICY encounters_panel_insert ON ehr.encounters FOR INSERT
    WITH CHECK (
        (SELECT ehr.is_clinician())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = encounters.patient_id AND a.ended_at IS NULL
        )
    );

-- The staff inbox (PHI). An item is its recipient's alone: they read it and mark it done, while their account is a
-- staff account that may sign in (a clinician or an admin). Nobody else sees it: not another clinician, not an admin
-- who isn't the recipient, not the patient. No runtime role inserts or deletes one (10-grants.sql): items are created
-- by the database's own triggers, such as a patient's message being delivered (90-messages.sql).
ALTER TABLE ehr.inbox_items ENABLE ROW LEVEL SECURITY;
ALTER TABLE ehr.inbox_items FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS inbox_items_own_select ON ehr.inbox_items;
CREATE POLICY inbox_items_own_select ON ehr.inbox_items FOR SELECT
    USING (((SELECT ehr.is_clinician()) OR (SELECT ehr.is_admin())) AND recipient_id = (SELECT ehr.current_user_id()));
DROP POLICY IF EXISTS inbox_items_own_update ON ehr.inbox_items;
CREATE POLICY inbox_items_own_update ON ehr.inbox_items FOR UPDATE
    USING (((SELECT ehr.is_clinician()) OR (SELECT ehr.is_admin())) AND recipient_id = (SELECT ehr.current_user_id()))
    WITH CHECK (((SELECT ehr.is_clinician()) OR (SELECT ehr.is_admin())) AND recipient_id = (SELECT ehr.current_user_id()));

-- Drafts are private to their author and require an active panel assignment.
ALTER TABLE ehr.clinical_drafts ENABLE ROW LEVEL SECURITY;
ALTER TABLE ehr.clinical_drafts FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS clinical_drafts_own ON ehr.clinical_drafts;
CREATE POLICY clinical_drafts_own ON ehr.clinical_drafts FOR ALL
    USING (
        (SELECT ehr.is_clinician())
        AND clinician_id = (SELECT ehr.current_user_id())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = clinical_drafts.patient_id AND a.ended_at IS NULL
        )
    );

-- Signed notes follow the chart's clinician access model. Drafts remain private under UC 8's panel policy.
ALTER TABLE ehr.clinical_notes ENABLE ROW LEVEL SECURITY;
ALTER TABLE ehr.clinical_notes FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS clinical_notes_clinician_select ON ehr.clinical_notes;
CREATE POLICY clinical_notes_clinician_select ON ehr.clinical_notes FOR SELECT
    USING ((SELECT ehr.is_clinician()));
DROP POLICY IF EXISTS clinical_notes_own_insert ON ehr.clinical_notes;
CREATE POLICY clinical_notes_own_insert ON ehr.clinical_notes FOR INSERT
    WITH CHECK ((SELECT ehr.is_clinician()) AND signer_id = (SELECT ehr.current_user_id()));

-- UC 1 (Tahseen Arnan). Orders (PHI) are read as signed notes are: by every clinician, on the chart's access model. A
-- clinician signs an order only as themselves and only for a patient on their active panel, the rule for writing notes;
-- the signing trigger also locks, checks for a duplicate and sets the time (60-orders.sql). The one update is
-- discontinuing an order, by the same rule: a clinician, for a patient on their active panel, recorded as themselves
-- (the trigger sets that before this check). Nothing deletes an order (10-grants.sql), so there is no policy for it.
ALTER TABLE ehr.orders ENABLE ROW LEVEL SECURITY;
ALTER TABLE ehr.orders FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS orders_clinician_select ON ehr.orders;
CREATE POLICY orders_clinician_select ON ehr.orders FOR SELECT
    USING ((SELECT ehr.is_clinician()));
DROP POLICY IF EXISTS orders_own_insert ON ehr.orders;
CREATE POLICY orders_own_insert ON ehr.orders FOR INSERT
    WITH CHECK (
        (SELECT ehr.is_clinician())
        AND signer_id = (SELECT ehr.current_user_id())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = orders.patient_id AND a.ended_at IS NULL
        )
    );
DROP POLICY IF EXISTS orders_panel_update ON ehr.orders;
CREATE POLICY orders_panel_update ON ehr.orders FOR UPDATE
    USING (
        (SELECT ehr.is_clinician())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = orders.patient_id AND a.ended_at IS NULL
        )
    )
    WITH CHECK (
        (SELECT ehr.is_clinician())
        AND discontinuer_id = (SELECT ehr.current_user_id())
        AND EXISTS (
            SELECT 1 FROM ehr.patient_assignments a
            WHERE a.clinician_id = (SELECT ehr.current_user_id())
              AND a.patient_id = orders.patient_id AND a.ended_at IS NULL
        )
    );

-- UC 13 review results are private to the clinician and the same active-panel draft as UC 14.
ALTER TABLE ehr.ai_note_reviews ENABLE ROW LEVEL SECURITY;
ALTER TABLE ehr.ai_note_reviews FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS ai_note_reviews_own ON ehr.ai_note_reviews;
CREATE POLICY ai_note_reviews_own ON ehr.ai_note_reviews FOR ALL
    USING ((SELECT ehr.is_clinician()) AND clinician_id = (SELECT ehr.current_user_id())
        AND EXISTS (SELECT 1 FROM ehr.clinical_drafts d
            WHERE d.id = ai_note_reviews.draft_id AND d.patient_id = ai_note_reviews.patient_id
              AND d.clinician_id = (SELECT ehr.current_user_id())));

ALTER TABLE ehr.ai_field_proposals ENABLE ROW LEVEL SECURITY;
ALTER TABLE ehr.ai_field_proposals FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS ai_field_proposals_own ON ehr.ai_field_proposals;
CREATE POLICY ai_field_proposals_own ON ehr.ai_field_proposals FOR ALL
    USING ((SELECT ehr.is_clinician()) AND EXISTS (SELECT 1 FROM ehr.ai_note_reviews r
        WHERE r.id = ai_field_proposals.review_id AND r.patient_id = ai_field_proposals.patient_id));
