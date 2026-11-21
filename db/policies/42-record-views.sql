-- Who viewed a patient's record (the expansion plan's step 13c, proposed FR-57): what the portal's "Who viewed my
-- record" page lists. Applied as ehr_owner after migrations, on every run of infra/migrate; safe to run again.
--
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
-- reads their record. A proxy whose access doesn't include sensitive items sees no view of sensitive items (a read that
-- showed them, or an event marked sensitive, 40-audit.sql), and no read of a note (a read of a note that was sensitive
-- already is recorded as a plain read of it, and notes never reach the portal); one without messages, no read of a
-- conversation. Those views simply aren't there for them: which lines exist, where a page ends and whether there is more
-- are decided from the lines the viewer may see and nothing else, so a page looks the same whether or not there are
-- views it leaves out.
--
-- Which part: a key from what the log records, the table read and the action, which the page puts in words. Most pages
-- record a read of the patient's own row, whatever part of the record they showed, so most views are 'record' until a
-- later step records which page was opened; when the log holds that, it wins over the table-and-action rule below.
--
-- Emergency access: a staff member's read is marked when it falls inside one of their emergency-access windows for the
-- patient (ehr.break_glass_accesses) and the patient wasn't on their panel at that moment (ehr.patient_assignments'
-- history). The reason given is never returned: it was written for the clinic's review.
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
CREATE OR REPLACE FUNCTION ehr.record_views(older_than timestamptz, newer_than timestamptz)
    RETURNS TABLE (occurred_at timestamptz, who text, name text, role text, part text, emergency boolean, more boolean)
    LANGUAGE sql
    STABLE
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
    WITH viewer AS MATERIALIZED (
        SELECT ehr.current_patient_id() AS subject, ehr.current_user_id() AS account, ehr.acting_grant_id() IS NULL AS own,
               ehr.portal_sees_sensitive() AS sees_sensitive, ehr.portal_may_message() AS sees_messages,
               now() - interval '12 months' AS since
    ),
    -- The events of the record this viewer is shown, before paging. Written once; each use below reads it through the
    -- index in its own order.
    shown AS NOT MATERIALIZED (
        SELECT e.id, e.occurred_at, e.action, e.actor_id, e.table_name
        FROM ehr.audit_events e
        WHERE e.patient_id = (SELECT v.subject FROM viewer v)
          AND e.occurred_at >= (SELECT v.since FROM viewer v)
          AND (e.action IN ('read', 'read_sensitive', 'download', 'download_sensitive') AND e.actor_id IS NOT NULL
                   AND ((SELECT v.own FROM viewer v) OR e.actor_id = (SELECT v.account FROM viewer v)
                        OR e.actor_id IN (SELECT s.id FROM ehr.staff_users s))
               OR e.action = 'sent_to_lab' AND e.actor_id IS NULL AND e.source = 'lab')
          AND ((SELECT v.sees_sensitive FROM viewer v)
               OR NOT (e.sensitive OR e.action IN ('read_sensitive', 'download_sensitive')
                       OR e.table_name IN ('clinical_notes', 'clinical_drafts', 'ai_note_reviews', 'ai_field_proposals')))
          AND ((SELECT v.sees_messages FROM viewer v) OR e.table_name IS DISTINCT FROM 'message_threads')
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
           -- The one rule from what the log records to a part. Anything it doesn't name is the record.
           CASE WHEN k.action = 'sent_to_lab' THEN 'lab-order'
                WHEN k.action = 'download' THEN 'download'
                WHEN k.action = 'download_sensitive' THEN 'download-sensitive'
                WHEN k.table_name = 'audit_events' THEN 'views'
                WHEN k.table_name = 'visit_summaries' THEN 'summary'
                WHEN k.table_name = 'message_threads' THEN 'messages'
                WHEN k.table_name IN ('clinical_notes', 'clinical_drafts', 'ai_note_reviews', 'ai_field_proposals') THEN 'note'
                WHEN k.action = 'read_sensitive' THEN 'record-sensitive'
                ELSE 'record' END,
           st.id IS NOT NULL
               AND EXISTS (SELECT 1 FROM ehr.break_glass_accesses b
                           WHERE b.clinician_id = k.actor_id AND b.patient_id = (SELECT v.subject FROM viewer v)
                             AND b.granted_at <= k.occurred_at AND k.occurred_at < b.expires_at)
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
