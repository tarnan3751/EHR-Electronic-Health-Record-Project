-- The write audit: every insert, update or delete of a row in a PHI table adds a row to ehr.audit_events, with the
-- actor from app.user_id, the row, its patient, and the values that changed. Applied as ehr_owner after migrations,
-- on every run of infra/migrate; safe to re-run. Reads are recorded by the PHI reader in Ehr.Domain, since
-- PostgreSQL has no SELECT trigger.
--
-- The function runs as its owner, ehr_owner (SECURITY DEFINER), so it can write the log whatever the caller may do.
-- Its search_path is pinned, so a caller can't put their own objects in front of the ones it uses, and nobody but
-- its owner may call it directly. Writes only ever happen on the primary, so the source is onprem, but for the writes
-- the lab's door makes (the expansion plan's step 12, 76-lab.sql): no account is behind those, and their source is lab.
-- A write to a sensitive item is marked as one, so the audit page shows that it changed and never its values (below).

CREATE OR REPLACE FUNCTION ehr.record_write() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    old_row jsonb := CASE WHEN TG_OP = 'INSERT' THEN NULL ELSE to_jsonb(OLD) END;
    new_row jsonb := CASE WHEN TG_OP = 'DELETE' THEN NULL ELSE to_jsonb(NEW) END;
    kept jsonb := coalesce(new_row, old_row);
    changed jsonb;
    -- A sensitive item, before or after the write: a diagnosis or medication marked sensitive.
    is_sensitive boolean := coalesce((new_row ->> 'sensitive')::boolean, false) OR coalesce((old_row ->> 'sensitive')::boolean, false);
BEGIN
    IF TG_OP = 'UPDATE' THEN
        -- Only the columns that changed, each with its old and new value.
        SELECT jsonb_object_agg(n.key, jsonb_build_object('old', o.value, 'new', n.value))
          INTO changed
          FROM jsonb_each(new_row) AS n JOIN jsonb_each(old_row) AS o ON o.key = n.key
         WHERE n.value IS DISTINCT FROM o.value;
    ELSE
        changed := jsonb_build_object(lower(TG_OP) || 'ed', kept);
    END IF;

    INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source, sensitive, details)
    VALUES (
        uuidv7(),
        now(),
        NULLIF(current_setting('app.user_id', true), '')::uuid,
        lower(TG_OP),
        TG_TABLE_NAME,
        (kept ->> 'id')::uuid,
        -- The patient a row belongs to: the row itself in ehr.patients, the patient_id column in every other PHI table.
        CASE WHEN TG_TABLE_NAME = 'patients' THEN (kept ->> 'id')::uuid ELSE (kept ->> 'patient_id')::uuid END,
        -- The lab's door says so for the rows it files (ehr.file_lab_results). Only with nobody signed in: a session
        -- that sets the same itself can't pass a signed-in account's write off as the lab's, and with nobody signed in
        -- no policy lets it write a patient's row.
        CASE WHEN current_setting('app.write_source', true) = 'lab' AND NULLIF(current_setting('app.user_id', true), '') IS NULL
             THEN 'lab' ELSE 'onprem' END,
        is_sensitive,
        changed);

    -- An item that is sensitive now is sensitive throughout its record: when it becomes sensitive, its earlier events,
    -- written before it was marked, are marked too, so the audit page stops showing their values. The one change an
    -- audit event ever takes (ehr.guard_audit_event below). Only on the write that marks the item, not on every write
    -- to a sensitive one: it reads the patient's events, by the index on the patient.
    IF TG_OP = 'UPDATE' AND coalesce((new_row ->> 'sensitive')::boolean, false) AND NOT coalesce((old_row ->> 'sensitive')::boolean, false)
       AND TG_TABLE_NAME <> 'patients' THEN
        UPDATE ehr.audit_events e SET sensitive = true
        WHERE e.patient_id = (kept ->> 'patient_id')::uuid AND e.table_name = TG_TABLE_NAME AND e.row_id = (kept ->> 'id')::uuid
          AND NOT e.sensitive;
    END IF;

    -- A note signed as sensitive (step 10) was written as a draft first, and perhaps dictated: the draft's events, and
    -- those of its dictation reviews and their proposed fields, hold the same text, written before anyone could know
    -- it would be sensitive. They are marked with the note, so the audit page stops showing their values too. Found
    -- through the audit events themselves (a review's insert names its draft, a proposal's its review), by the index
    -- on the patient: this function reads no patient table.
    -- The same when a signed note is marked sensitive afterwards (step 10b).
    IF TG_TABLE_NAME = 'clinical_notes' AND coalesce((new_row ->> 'sensitive')::boolean, false)
       AND (TG_OP = 'INSERT' OR TG_OP = 'UPDATE' AND NOT coalesce((old_row ->> 'sensitive')::boolean, false)) THEN
        UPDATE ehr.audit_events e SET sensitive = true
        WHERE e.patient_id = (kept ->> 'patient_id')::uuid AND NOT e.sensitive
          AND (e.table_name = 'clinical_drafts' AND e.row_id = (kept ->> 'draft_id')::uuid
               OR e.table_name = 'ai_note_reviews' AND e.row_id IN (
                    SELECT r.row_id FROM ehr.audit_events r
                    WHERE r.patient_id = (kept ->> 'patient_id')::uuid AND r.table_name = 'ai_note_reviews' AND r.action = 'insert'
                      AND r.details -> 'inserted' ->> 'draft_id' = kept ->> 'draft_id')
               OR e.table_name = 'ai_field_proposals' AND e.row_id IN (
                    SELECT p.row_id FROM ehr.audit_events p
                    WHERE p.patient_id = (kept ->> 'patient_id')::uuid AND p.table_name = 'ai_field_proposals' AND p.action = 'insert'
                      AND p.details -> 'inserted' ->> 'review_id' IN (
                          SELECT r.row_id::text FROM ehr.audit_events r
                          WHERE r.patient_id = (kept ->> 'patient_id')::uuid AND r.table_name = 'ai_note_reviews' AND r.action = 'insert'
                            AND r.details -> 'inserted' ->> 'draft_id' = kept ->> 'draft_id')));
    END IF;

    RETURN NULL;
END
$$;

REVOKE ALL ON FUNCTION ehr.record_write() FROM PUBLIC;

-- An audit event is never edited. The one change it takes is being marked sensitive, by the trigger above, when the
-- item it is about becomes sensitive; nothing unmarks it, and no other column moves. The runtime roles have no UPDATE
-- at all (10-grants.sql); this holds for the owner too.
CREATE OR REPLACE FUNCTION ehr.guard_audit_event() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF OLD.sensitive OR NOT NEW.sensitive OR (to_jsonb(NEW) - 'sensitive') IS DISTINCT FROM (to_jsonb(OLD) - 'sensitive') THEN
        RAISE EXCEPTION 'An audit event is never edited' USING ERRCODE = '42501';
    END IF;

    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.guard_audit_event() FROM PUBLIC;
CREATE OR REPLACE TRIGGER audit_events_guard
    BEFORE UPDATE ON ehr.audit_events
    FOR EACH ROW EXECUTE FUNCTION ehr.guard_audit_event();

-- What an admin may see of what a write changed. The runtime roles can't read audit_events.details (10-grants.sql):
-- the audit page asks here, for the events it lists. It answers only an admin, and never with the values of a
-- sensitive item, or of the lists' history rows, which copy those items: for those the page says that a sensitive
-- item changed, and who changed it, from the event's other columns. The full record stays in the table. Runs as its
-- owner, to read the column; its search_path is pinned, and only the runtime roles may call it.
CREATE OR REPLACE FUNCTION ehr.shown_audit_details(event_ids uuid[]) RETURNS TABLE (id uuid, details jsonb)
    LANGUAGE sql
    STABLE
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
    SELECT e.id, e.details
    FROM ehr.audit_events e
    WHERE e.id = ANY(event_ids) AND (SELECT ehr.is_admin()) AND NOT e.sensitive
      -- Nor the text of what a patient and their care team wrote to each other.
      AND e.table_name IS DISTINCT FROM 'chart_history' AND e.table_name IS DISTINCT FROM 'messages' AND e.table_name IS DISTINCT FROM 'message_threads'
      AND e.details IS NOT NULL
$$;
REVOKE ALL ON FUNCTION ehr.shown_audit_details(uuid[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ehr.shown_audit_details(uuid[]) TO ehr_app, ehr_read;

-- Events written before an item was marked, or before this rule: marked now, on every pass, as 65-chart-lists.sql
-- marks the diagnoses themselves. Forced row-level security hides the two lists' rows from this role, so it is lifted
-- on them for the statement, inside a transaction; no other session sees them without it. It isn't lifted on
-- audit_events: the owner's own policies (30-rls.sql) let it find and mark events, and locking that table here, before
-- the lists, could deadlock with a page that has read a list and is about to record its read.
BEGIN;
ALTER TABLE ehr.conditions NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ehr.medication_statements NO FORCE ROW LEVEL SECURITY;
UPDATE ehr.audit_events e SET sensitive = true
WHERE NOT e.sensitive
  AND (e.table_name = 'conditions' AND EXISTS (SELECT 1 FROM ehr.conditions c WHERE c.id = e.row_id AND c.sensitive)
       OR e.table_name = 'medication_statements' AND EXISTS (SELECT 1 FROM ehr.medication_statements m WHERE m.id = e.row_id AND m.sensitive));
ALTER TABLE ehr.medication_statements FORCE ROW LEVEL SECURITY;
ALTER TABLE ehr.conditions FORCE ROW LEVEL SECURITY;
COMMIT;

-- Every PHI table gets this trigger; Ehr.Security.Tests fails for one that doesn't.
CREATE OR REPLACE TRIGGER patients_audit
    AFTER INSERT OR UPDATE OR DELETE ON ehr.patients
    FOR EACH ROW EXECUTE FUNCTION ehr.record_write();

CREATE OR REPLACE TRIGGER patient_assignments_audit
    AFTER INSERT OR UPDATE OR DELETE ON ehr.patient_assignments
    FOR EACH ROW EXECUTE FUNCTION ehr.record_write();

CREATE OR REPLACE TRIGGER break_glass_accesses_audit
    AFTER INSERT OR UPDATE OR DELETE ON ehr.break_glass_accesses
    FOR EACH ROW EXECUTE FUNCTION ehr.record_write();

CREATE OR REPLACE TRIGGER allergy_intolerances_audit
    AFTER INSERT OR UPDATE OR DELETE ON ehr.allergy_intolerances
    FOR EACH ROW EXECUTE FUNCTION ehr.record_write();

CREATE OR REPLACE TRIGGER medication_statements_audit
    AFTER INSERT OR UPDATE OR DELETE ON ehr.medication_statements
    FOR EACH ROW EXECUTE FUNCTION ehr.record_write();

CREATE OR REPLACE TRIGGER conditions_audit
    AFTER INSERT OR UPDATE OR DELETE ON ehr.conditions
    FOR EACH ROW EXECUTE FUNCTION ehr.record_write();

CREATE OR REPLACE TRIGGER encounters_audit
    AFTER INSERT OR UPDATE OR DELETE ON ehr.encounters
    FOR EACH ROW EXECUTE FUNCTION ehr.record_write();

CREATE OR REPLACE TRIGGER clinical_drafts_audit
    AFTER INSERT OR UPDATE OR DELETE ON ehr.clinical_drafts
    FOR EACH ROW EXECUTE FUNCTION ehr.record_write();

CREATE OR REPLACE TRIGGER clinical_notes_audit
    AFTER INSERT OR UPDATE OR DELETE ON ehr.clinical_notes
    FOR EACH ROW EXECUTE FUNCTION ehr.record_write();

CREATE OR REPLACE TRIGGER note_addenda_audit
    AFTER INSERT OR UPDATE OR DELETE ON ehr.note_addenda
    FOR EACH ROW EXECUTE FUNCTION ehr.record_write();

CREATE OR REPLACE TRIGGER ai_note_reviews_audit
    AFTER INSERT OR UPDATE OR DELETE ON ehr.ai_note_reviews
    FOR EACH ROW EXECUTE FUNCTION ehr.record_write();
CREATE OR REPLACE TRIGGER ai_field_proposals_audit
    AFTER INSERT OR UPDATE OR DELETE ON ehr.ai_field_proposals
    FOR EACH ROW EXECUTE FUNCTION ehr.record_write();

CREATE OR REPLACE TRIGGER chart_history_audit
    AFTER INSERT OR UPDATE OR DELETE ON ehr.chart_history
    FOR EACH ROW EXECUTE FUNCTION ehr.record_write();

CREATE OR REPLACE TRIGGER result_acknowledgments_audit
    AFTER INSERT OR UPDATE OR DELETE ON ehr.result_acknowledgments
    FOR EACH ROW EXECUTE FUNCTION ehr.record_write();

CREATE OR REPLACE TRIGGER proxy_grants_audit
    AFTER INSERT OR UPDATE OR DELETE ON ehr.proxy_grants
    FOR EACH ROW EXECUTE FUNCTION ehr.record_write();

CREATE OR REPLACE TRIGGER message_threads_audit
    AFTER INSERT OR UPDATE OR DELETE ON ehr.message_threads
    FOR EACH ROW EXECUTE FUNCTION ehr.record_write();

CREATE OR REPLACE TRIGGER messages_audit
    AFTER INSERT OR UPDATE OR DELETE ON ehr.messages
    FOR EACH ROW EXECUTE FUNCTION ehr.record_write();

CREATE OR REPLACE TRIGGER appointments_audit
    AFTER INSERT OR UPDATE OR DELETE ON ehr.appointments
    FOR EACH ROW EXECUTE FUNCTION ehr.record_write();

CREATE OR REPLACE TRIGGER inbox_items_audit
    AFTER INSERT OR UPDATE OR DELETE ON ehr.inbox_items
    FOR EACH ROW EXECUTE FUNCTION ehr.record_write();

CREATE OR REPLACE TRIGGER orders_audit
    AFTER INSERT OR UPDATE OR DELETE ON ehr.orders
    FOR EACH ROW EXECUTE FUNCTION ehr.record_write();

CREATE OR REPLACE TRIGGER observations_audit
    AFTER INSERT OR UPDATE OR DELETE ON ehr.observations
    FOR EACH ROW EXECUTE FUNCTION ehr.record_write();
