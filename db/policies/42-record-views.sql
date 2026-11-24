-- Who viewed a patient's record (the expansion plan's step 13c, proposed FR-57): what the portal's "Who viewed my
-- record" page lists, and, at the end, the guard on the panel history it relies on. Applied as ehr_owner after
-- migrations, on every run of infra/migrate; safe to run again.

-- The part of the record a view was of, from the action and the table the log records, or NULL for a table it doesn't
-- name: what decides which views a viewer is shown, and what names a view recorded without its part (before step 13c2,
-- or by an app older than its migration). Every table of patient data is named here (RecordViewSecurityTests holds it
-- to the list ProtectionTests keeps), so a new one is a decision: which part, and whether a proxy without sensitive
-- items may see a view of it. Reads only its arguments, so PostgreSQL writes it into ehr.record_views' own query; nobody
-- else may run it.
CREATE OR REPLACE FUNCTION ehr.record_view_part(action text, table_name text) RETURNS text
    LANGUAGE sql
    IMMUTABLE
AS $$
    SELECT CASE
        WHEN action = 'sent_to_lab' THEN 'lab-order'
        WHEN action = 'download' THEN 'download'
        WHEN action = 'download_sensitive' THEN 'download-sensitive'
        WHEN table_name = 'audit_events' THEN 'views'
        WHEN table_name = 'patients' THEN CASE WHEN action = 'read_sensitive' THEN 'record-sensitive' ELSE 'record' END
        WHEN table_name = 'visit_summaries' THEN 'summary'
        WHEN table_name IN ('message_threads', 'messages') THEN 'messages'
        -- A dictation's proposed orders and allergies (step 15) are its review's words: a note being written. So is a
        -- clinician's consent to record a visit and the visit's transcript (step 16).
        WHEN table_name IN ('clinical_notes', 'note_addenda', 'clinical_drafts', 'ai_note_reviews', 'ai_field_proposals',
                            'ai_chart_proposals', 'ambient_consents', 'ambient_transcripts') THEN 'note'
        -- Pre-visit forms (step 13e), sensitive items always: a part of their own, the one their pages' reads are recorded
        -- with too (step 13c2), which a proxy without sensitive items is never shown a view of, by either.
        WHEN table_name IN ('questionnaire_requests', 'questionnaire_responses') THEN 'forms'
        -- The rest of the chart, and what the clinic keeps about the patient: the record. A refill request (step 13d) has
        -- no read of its own: its pages record a read of the patient, with the refill requests' part (step 13c2), which a
        -- proxy without messages, as asking is, or without sensitive items is told as the record.
        WHEN table_name IN ('patient_assignments', 'break_glass_accesses', 'allergy_intolerances', 'medication_statements', 'conditions',
                            'chart_history', 'inbox_items', 'appointments', 'proxy_grants', 'result_acknowledgments', 'encounters',
                            'orders', 'observations', 'refill_requests') THEN 'record'
    END
$$;
REVOKE ALL ON FUNCTION ehr.record_view_part(text, text) FROM PUBLIC;

-- A portal session can't read ehr.audit_events: only admins have a policy for it (30-rls.sql), and none is added here.
-- This function is the one door, and it answers only for the record the session is looking at
-- (ehr.current_patient_id): the patient's own, or the one a proxy's grant in force names. It takes no patient. Anyone
-- else (a clinician, an admin, nobody, a grant that has ended or isn't theirs) gets nothing.
--
-- What it lists: the reads of that record in the last 12 months, and each lab order handed to the lab. A read is
-- `read`, `read_sensitive`, or step 13b's `download` and `download_sensitive`, by a staff or portal account; a hand-over
-- is `sent_to_lab`, with no account and the lab as the source (76-lab.sql). Nothing else: no write, sign-in, account or
-- system event, and no emergency-access reason. For each, who (the viewer themselves, a staff member by the name and role
-- their account has now, a proxy by the address their access was given to, or the lab), when, which part, and whether it
-- was made under emergency access; never an ID of any kind, a table name, a reason or a write's values.
--
-- Who sees which lines. The patient sees them all. A proxy sees staff members' views, the lab's, and their own: never
-- the patient's own nor another proxy's, so the page can't tell them that someone else has access or when the patient
-- reads their record. A proxy the patient allowed sensitive items sees every staff member's view, emergency access marked,
-- as the patient does. A proxy without sensitive items is shown a staff member's view only when that staff member had the
-- patient on their panel at that moment (ehr.patient_assignments' history): a view from outside the care team, such as
-- one through emergency access, may tell of something the patient chose not to share, so it is treated as a view of
-- sensitive items. Nor are they shown a view of sensitive items (a read that showed them, or an event marked sensitive,
-- 40-audit.sql), a read of a note (a read of a note that was sensitive already is recorded as a plain read of it, and
-- notes never reach the portal), a view of any part the rule below doesn't name, or a view of a pre-visit form (step
-- 13e), which is always sensitive. A proxy without messages is shown no read of a conversation. Those views simply aren't
-- there for them: which lines exist, where a page ends and whether there is more are decided from the lines the viewer
-- may see and nothing else, so a page looks the same whether or not there are views it leaves out.
--
-- Which part: the part of the record the read was recorded for (ehr.audit_events.part, step 13c2: the page or API that
-- read it, one of Ehr.Data's AuditParts), which the page puts in words, and whether the view showed the patient's
-- sensitive items. The part names a line: which lines a viewer is shown is decided as above, by the table and the
-- action, whatever the part, but for a pre-visit form's, which is never shown to a proxy without sensitive items. A
-- viewer is told the part only when it can tell them nothing they may not see: the patient and a proxy with sensitive
-- items every part, but a conversation or a refill request (step 13d) only with messages, as asking is; a proxy without
-- sensitive items only the parts below that are no hint of a change or an item kept from them, and "the record" for the
-- rest (the lists, orders, the inbox, who the record is shared with, a note's pages, refill requests), as every line
-- was before that step. A view recorded without a part (before step 13c2, by an app older than its migration, or the
-- lab's hand-over) is named from the table and the action (ehr.record_view_part, above). Every table of patient data is
-- named there, and a test fails for one that isn't: a view of a table nobody named is the record to the patient and
-- hidden from a proxy without sensitive items.
--
-- Emergency access: a staff member's read is marked when it falls inside one of their emergency-access windows for the
-- patient (ehr.break_glass_accesses) and the patient wasn't on their panel at that moment (ehr.patient_assignments'
-- history, the same moment test as above). The reason given is never returned: it was written for the clinic's review.
--
-- Paging: 50 lines a page, newest first, from now or older than a moment (older_than), or the 50 just newer than a moment
-- (newer_than) when only that is given. A page holds whole moments: the events recorded at one instant are never split
-- between two pages, so a moment is all the next page needs to start from, and no ID reaches the portal. `more` says
-- whether there are more lines past the page in the direction asked. Every line comes from the index on the patient and
-- the time (ix_audit_events_patient_id_occurred_at), the 12 months bounding how far a page can look.
--
-- Runs as its owner, reading the log, the emergency-access windows, the panels and the proxy grants through the owner's
-- policies (30-rls.sql), and the account tables, which no policy covers; its search_path is pinned, and only the runtime
-- roles may call it.
-- Replaced in place on every run, so it keeps its identity and a page calling it as the file runs is never refused. Once,
-- on the upgrade to step 13c2, it is dropped first, since its result gained a column (sensitive), which CREATE OR
-- REPLACE can't change: in the same transaction as the rest, so no page finds it missing, though a call made in that
-- one moment can fail, once (docs/UC2.md says so for a deploy). A function already of this shape is never dropped.
BEGIN;
DO $drop$
BEGIN
    IF EXISTS (SELECT FROM pg_proc p
               WHERE p.oid = to_regprocedure('ehr.record_views(timestamptz, timestamptz)') AND NOT 'sensitive' = ANY (p.proargnames)) THEN
        DROP FUNCTION ehr.record_views(timestamptz, timestamptz);
    END IF;
END
$drop$;
CREATE OR REPLACE FUNCTION ehr.record_views(older_than timestamptz, newer_than timestamptz)
    RETURNS TABLE (occurred_at timestamptz, who text, name text, role text, part text, sensitive boolean, emergency boolean, more boolean)
    LANGUAGE sql
    STABLE
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
    -- Who is looking, worked out once, by itself: kept apart so PostgreSQL can't write these calls into the scan below,
    -- where they would run for every row of the panels' table (a review measured 0.6 s a page at 60,000 rows).
    WITH who AS MATERIALIZED (
        SELECT ehr.current_patient_id() AS subject, ehr.current_user_id() AS account, ehr.acting_grant_id() IS NULL AS own,
               ehr.portal_sees_sensitive() AS sees_sensitive, ehr.portal_may_message() AS sees_messages,
               now() - interval '12 months' AS since
    ),
    -- And, for a proxy without sensitive items, the patient's panel history: who had the patient on their panel, from
    -- when and until when, a few rows. It is read for every such proxy's page, whatever the page holds, before anything
    -- else, so the page's time doesn't depend on whether there are views it leaves out (a review found that reading it
    -- only when a staff member's view needed it told a proxy, by the page's time, that one such view existed). By the
    -- index on the patient and clinician (ix_patient_assignments_history, step 13c2), which holds ended assignments too.
    viewer AS MATERIALIZED (
        SELECT c.*, h.panel_clinicians, h.panel_from, h.panel_until
        FROM who c
        CROSS JOIN LATERAL (
            SELECT array_agg(a.clinician_id) AS panel_clinicians, array_agg(a.assigned_at) AS panel_from,
                   array_agg(coalesce(a.ended_at, 'infinity')) AS panel_until
            FROM ehr.patient_assignments a
            WHERE NOT c.sees_sensitive AND a.patient_id = c.subject) h
    ),
    -- The events of the record this viewer is shown, before paging. Written once; each use below reads it through the
    -- index in its own order.
    shown AS NOT MATERIALIZED (
        SELECT e.id, e.occurred_at, e.action, e.actor_id, e.table_name, e.part
        FROM ehr.audit_events e
        WHERE e.patient_id = (SELECT v.subject FROM viewer v)
          AND e.occurred_at >= (SELECT v.since FROM viewer v)
          AND (e.action IN ('read', 'read_sensitive', 'download', 'download_sensitive') AND e.actor_id IS NOT NULL
                   AND ((SELECT v.own FROM viewer v) OR e.actor_id = (SELECT v.account FROM viewer v)
                        OR e.actor_id IN (SELECT s.id FROM ehr.staff_users s))
               OR e.action = 'sent_to_lab' AND e.actor_id IS NULL AND e.source = 'lab')
          AND ((SELECT v.sees_messages FROM viewer v) OR ehr.record_view_part(e.action, e.table_name) IS DISTINCT FROM 'messages')
          -- Without sensitive items: a view of no sensitive item, of a part the rule names, and, from a staff member, only
          -- while the patient was on their panel, which also leaves out every view marked as emergency access.
          AND ((SELECT v.sees_sensitive FROM viewer v)
               OR NOT e.sensitive AND e.action NOT IN ('read_sensitive', 'download_sensitive')
                  AND ehr.record_view_part(e.action, e.table_name) IN ('record', 'download', 'summary', 'messages', 'views', 'lab-order')
                  AND e.part IS DISTINCT FROM 'forms'
                  AND (e.actor_id IS NULL OR e.actor_id = (SELECT v.account FROM viewer v)
                       OR EXISTS (SELECT 1 FROM viewer v, unnest(v.panel_clinicians, v.panel_from, v.panel_until) AS p(clinician, from_at, until_at)
                                  WHERE p.clinician = e.actor_id AND p.from_at <= e.occurred_at AND e.occurred_at < p.until_at)))
    ),
    -- The first 51 in the direction asked: the 51st only says whether there is more.
    page AS (
        (SELECT s.* FROM shown s
         WHERE (older_than IS NOT NULL OR newer_than IS NULL) AND s.occurred_at < coalesce(older_than, 'infinity')
         ORDER BY s.occurred_at DESC LIMIT 51)
        UNION ALL
        (SELECT s.* FROM shown s
         WHERE older_than IS NULL AND newer_than IS NOT NULL AND s.occurred_at > newer_than
         ORDER BY s.occurred_at LIMIT 51)
    ),
    edge AS (
        SELECT count(*) AS counted, min(p.occurred_at) AS earliest, max(p.occurred_at) AS latest,
               CASE WHEN older_than IS NOT NULL OR newer_than IS NULL THEN min(p.occurred_at) ELSE max(p.occurred_at) END AS far
        FROM page p
    ),
    -- Whole moments: with more to come, the farthest moment is left to the next page, unless it is all the page holds,
    -- when the page takes all of that moment instead.
    kept AS (
        SELECT p.* FROM page p, edge g
        WHERE g.counted < 51 OR (g.earliest < g.latest AND p.occurred_at <> g.far)
        UNION ALL
        SELECT s.* FROM edge g, shown s
        WHERE g.counted = 51 AND g.earliest = g.latest AND s.occurred_at = g.far
    )
    SELECT k.occurred_at,
           CASE WHEN k.actor_id IS NULL THEN 'lab'
                WHEN k.actor_id = (SELECT v.account FROM viewer v) THEN 'you'
                WHEN st.id IS NOT NULL THEN 'staff'
                ELSE 'proxy' END,
           -- A staff member's name as their account has it now; a proxy's address as the patient's Access page shows it.
           CASE WHEN k.actor_id = (SELECT v.account FROM viewer v) THEN NULL
                WHEN st.id IS NOT NULL THEN st.display_name::text
                ELSE lower(pu.normalized_email) END,
           CASE WHEN st.id IS NULL THEN NULL
                WHEN st.roles @> ARRAY['Clinician'] THEN 'clinician'
                WHEN st.roles @> ARRAY['Admin'] THEN 'administrator'
                ELSE 'staff' END,
           -- The part recorded with the read, to a viewer who may be told it (above), or "the record". Without one, from
           -- the table and the action, where a table the rule doesn't name is the record; only the patient and proxies
           -- with sensitive items get there.
           CASE WHEN k.part IS NULL THEN coalesce(ehr.record_view_part(k.action, k.table_name), 'record')
                WHEN k.part IN ('conversations', 'refills') AND NOT (SELECT v.sees_messages FROM viewer v) THEN 'record'
                WHEN (SELECT v.sees_sensitive FROM viewer v)
                     OR k.part IN ('chart', 'results', 'vitals', 'search', 'panel', 'schedule', 'appointments', 'home', 'conversations',
                                   'offline', 'fhir', 'summaries', 'printable', 'download', 'views') THEN k.part
                ELSE 'record' END,
           -- Whether the view showed the patient's sensitive items, which only the patient and proxies with them are shown.
           k.action IN ('read_sensitive', 'download_sensitive'),
           st.id IS NOT NULL
               AND EXISTS (SELECT 1 FROM ehr.break_glass_accesses b
                           WHERE b.clinician_id = k.actor_id AND b.patient_id = (SELECT v.subject FROM viewer v)
                             AND b.granted_at <= k.occurred_at AND k.occurred_at < b.expires_at)
               -- On the panel at that moment: the same test as the panel history's above, against the table, since that
               -- history is read above only for a proxy without sensitive items; by the same index, so a clinician's
               -- thousands of assignments aren't read for each line.
               AND NOT EXISTS (SELECT 1 FROM ehr.patient_assignments a
                               WHERE a.clinician_id = k.actor_id AND a.patient_id = (SELECT v.subject FROM viewer v)
                                 AND a.assigned_at <= k.occurred_at AND (a.ended_at IS NULL OR a.ended_at > k.occurred_at)),
           (SELECT g.counted FROM edge g) = 51
    FROM kept k
    LEFT JOIN ehr.staff_users st ON st.id = k.actor_id
    LEFT JOIN ehr.patient_users pu ON pu.id = k.actor_id AND st.id IS NULL
    ORDER BY k.occurred_at DESC, k.id DESC
$$;
REVOKE ALL ON FUNCTION ehr.record_views(timestamptz, timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ehr.record_views(timestamptz, timestamptz) TO ehr_app, ehr_read;
COMMIT;

-- The panel's history decides which staff members' views a proxy without sensitive items is shown (above), so the apps
-- can't rewrite it: an assignment the runtime roles insert starts now and open, and the one change it takes is ending,
-- now, once. The app stamps both times from its own clock a moment before saving (Ehr.Domain's PatientAssignments: the
-- chart's "Add to my patients" and "Remove", and the benchmark's setup), on the on-prem app beside this database; off
-- site, the cloud app forwards the write there. Nothing else writes the table at runtime: neither the desktop app nor
-- the sync API holds or replays assignments. A time within a minute of this server's clock is kept, so it stays on the
-- clock the app stamps its views with; one further off, as from a machine whose clock slipped while it slept, is made
-- this server's clock, as a proxy grant's is (95-proxy-access.sql), rather than refused, so no one is kept from their
-- own panel by a clock: either way an assignment can't be backdated or dated ahead by more than the minute. A row
-- inserted already ended, a change to its ID, patient, clinician or start, a second end or a reopening are refused.
-- Rows the schema's owner writes (the migrations, the seed) or a superuser (a test's setup) aren't held to it. Each
-- write is audited as before (40-audit.sql).
CREATE OR REPLACE FUNCTION ehr.guard_patient_assignment() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.ended_at IS NOT NULL THEN
            RAISE EXCEPTION 'A panel assignment starts open' USING ERRCODE = '23514';
        END IF;

        IF abs(extract(epoch FROM NEW.assigned_at - clock_timestamp())) > 60 THEN
            NEW.assigned_at := clock_timestamp();
        END IF;
    ELSIF (NEW.id, NEW.patient_id, NEW.clinician_id, NEW.assigned_at) IS DISTINCT FROM (OLD.id, OLD.patient_id, OLD.clinician_id, OLD.assigned_at)
          OR OLD.ended_at IS NOT NULL AND NEW.ended_at IS DISTINCT FROM OLD.ended_at THEN
        RAISE EXCEPTION 'A panel assignment only ends, once' USING ERRCODE = '23514';
    ELSIF OLD.ended_at IS NULL AND NEW.ended_at IS NOT NULL THEN
        IF abs(extract(epoch FROM NEW.ended_at - clock_timestamp())) > 60 THEN
            NEW.ended_at := clock_timestamp();
        END IF;

        -- A start and an end each within the minute could still leave the end before the start: it is never before it.
        NEW.ended_at := greatest(NEW.ended_at, OLD.assigned_at);
    END IF;

    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.guard_patient_assignment() FROM PUBLIC;
CREATE OR REPLACE TRIGGER patient_assignments_guard
    BEFORE INSERT OR UPDATE ON ehr.patient_assignments
    -- By the role the session logged in as, or the one a statement runs as, as for filing a result (75-results.sql).
    FOR EACH ROW WHEN (current_user IN ('ehr_app', 'ehr_read') OR session_user IN ('ehr_app', 'ehr_read'))
    EXECUTE FUNCTION ehr.guard_patient_assignment();
