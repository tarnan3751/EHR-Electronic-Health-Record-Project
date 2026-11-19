-- UC 14: signing and edits serialize on the SAME draft row. These invoker functions respect RLS and grants.
-- No runtime DELETE grant on signed notes, and UPDATE on their sensitivity alone (step 10b), with triggers as an
-- additional immutability boundary: a signed note's text never changes.
CREATE OR REPLACE FUNCTION ehr.guard_clinical_draft() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.patient_id IS DISTINCT FROM OLD.patient_id
       OR NEW.clinician_id IS DISTINCT FROM OLD.clinician_id THEN
        RAISE EXCEPTION 'A draft cannot be moved to another patient or author' USING ERRCODE = '23514';
    END IF;
    IF EXISTS (SELECT 1 FROM ehr.clinical_notes WHERE draft_id = OLD.id) THEN
        RAISE EXCEPTION 'The draft is signed and cannot be edited' USING ERRCODE = '23514';
    END IF;
    IF NEW.version <> OLD.version + 1 THEN
        RAISE EXCEPTION 'A draft update must advance its version' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.guard_clinical_draft() FROM PUBLIC;
CREATE OR REPLACE TRIGGER clinical_drafts_guard
    BEFORE UPDATE ON ehr.clinical_drafts
    FOR EACH ROW EXECUTE FUNCTION ehr.guard_clinical_draft();

CREATE OR REPLACE FUNCTION ehr.finalize_clinical_note() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    draft ehr.clinical_drafts%ROWTYPE;
BEGIN
    -- RLS requires the current clinician to own this draft and still have its patient on their panel.
    SELECT * INTO draft FROM ehr.clinical_drafts WHERE id = NEW.draft_id FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'The draft is unavailable' USING ERRCODE = '23514';
    END IF;
    IF NEW.patient_id <> draft.patient_id OR NEW.draft_version <> draft.version
       OR draft.structured_version IS DISTINCT FROM draft.version
       OR NEW.history IS DISTINCT FROM draft.history OR NEW.assessment IS DISTINCT FROM draft.assessment
       OR NEW.plan IS DISTINCT FROM draft.plan THEN
        RAISE EXCEPTION 'The draft changed; review the saved version before signing' USING ERRCODE = '23514';
    END IF;
    -- The cached flag is never sufficient proof. Consult immutable bindings and authoritative decisions too.
    IF draft.has_unreviewed_proposals
       OR EXISTS (SELECT 1 FROM ehr.ai_note_reviews r WHERE r.draft_id = draft.id AND r.state IN ('recording', 'pending'))
       OR EXISTS (SELECT 1 FROM ehr.ai_field_proposals p JOIN ehr.ai_note_reviews r ON r.id = p.review_id
                  WHERE r.draft_id = draft.id AND p.state = 'pending')
       OR EXISTS (SELECT 1 FROM ehr.ai_note_reviews r WHERE r.draft_id = draft.id AND r.state = 'completed'
                  AND (r.imported_version IS NULL OR r.imported_version > draft.version)) THEN
        RAISE EXCEPTION 'Associated AI proposals require server-side review' USING ERRCODE = '23514';
    END IF;
    IF draft.history IS NULL OR draft.assessment IS NULL OR draft.plan IS NULL
       OR draft.history !~ '[^[:space:]]' OR draft.assessment !~ '[^[:space:]]' OR draft.plan !~ '[^[:space:]]'
       OR char_length(draft.history) > 16000 OR char_length(draft.assessment) > 16000
       OR char_length(draft.plan) > 16000 OR octet_length(draft.body) > 65536 THEN
        RAISE EXCEPTION 'Complete History, Assessment and Plan within note limits before signing' USING ERRCODE = '23514';
    END IF;
    -- Never trust client identity or time. The signed snapshot retains the version the clinician confirmed.
    NEW.signer_id := ehr.current_user_id();
    NEW.signed_at := clock_timestamp();
    -- Signed as sensitive (step 10): its signer is the person who made it so (step 10b), with no reason to give.
    NEW.sensitive := coalesce(NEW.sensitive, false);
    NEW.sensitive_marked_by := CASE WHEN NEW.sensitive THEN ehr.current_user_id() END;
    NEW.sensitive_reason := NULL;
    NEW.sensitive_changed_at := NULL;
    -- Advance the sync watermark without changing Body's plain-text contract. Subsequent edits are blocked.
    UPDATE ehr.clinical_drafts SET version = version + 1, structured_version = version + 1 WHERE id = draft.id;
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.finalize_clinical_note() FROM PUBLIC;
CREATE OR REPLACE TRIGGER clinical_notes_finalize
    BEFORE INSERT ON ehr.clinical_notes
    FOR EACH ROW EXECUTE FUNCTION ehr.finalize_clinical_note();

CREATE OR REPLACE FUNCTION ehr.reject_clinical_note_change() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    RAISE EXCEPTION 'Signed clinical notes are immutable' USING ERRCODE = '23514';
END
$$;
REVOKE ALL ON FUNCTION ehr.reject_clinical_note_change() FROM PUBLIC;
-- A signed note is never deleted. Its one update is its sensitivity (ehr.change_note_sensitivity, below).
CREATE OR REPLACE TRIGGER clinical_notes_immutable
    BEFORE DELETE ON ehr.clinical_notes
    FOR EACH ROW EXECUTE FUNCTION ehr.reject_clinical_note_change();

-- Marking a signed note sensitive after signing, or unmarking it (the expansion plan's step 10b, Tahseen's decision of
-- 19 November 2026). The one change a signed note takes; its text never moves. Whoever makes the update, the tables'
-- owner included: nothing changes but the sensitivity; it must actually change; a reason is required; who and when are
-- the database's. Anyone the policy lets update the note (a clinician with the patient on their panel) may mark it.
-- Only the person who made it sensitive, or the note's signer, may unmark it. Runs as the caller.
CREATE OR REPLACE FUNCTION ehr.change_note_sensitivity() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    apart constant text[] := ARRAY['sensitive', 'sensitive_marked_by', 'sensitive_reason', 'sensitive_changed_at'];
BEGIN
    IF (to_jsonb(NEW) - apart) IS DISTINCT FROM (to_jsonb(OLD) - apart) THEN
        RAISE EXCEPTION 'Signed clinical notes are immutable' USING ERRCODE = '23514';
    END IF;
    -- One at a time with an addendum being signed to this note (ehr.sign_note_addendum).
    PERFORM pg_advisory_xact_lock(hashtextextended('ehr.clinical_notes:' || OLD.id::text, 0));
    IF NEW.sensitive IS NOT DISTINCT FROM OLD.sensitive THEN
        RAISE EXCEPTION 'A signed note changes only by being marked sensitive or unmarked' USING ERRCODE = '23514';
    END IF;
    NEW.sensitive_reason := btrim(NEW.sensitive_reason);
    IF NEW.sensitive_reason IS NULL OR NEW.sensitive_reason !~ '[^[:space:]]' OR char_length(NEW.sensitive_reason) > 500 THEN
        RAISE EXCEPTION 'Say why, in up to 500 characters' USING ERRCODE = '23514';
    END IF;

    IF NEW.sensitive THEN
        IF ehr.current_user_id() IS NULL THEN
            RAISE EXCEPTION 'A note is marked sensitive by someone' USING ERRCODE = '42501';
        END IF;
        NEW.sensitive_marked_by := ehr.current_user_id();
    ELSE
        -- A note marked before anyone was recorded as marking it was marked by its signer.
        IF ehr.current_user_id() IS NULL
           OR (ehr.current_user_id() IS DISTINCT FROM coalesce(OLD.sensitive_marked_by, OLD.signer_id) AND ehr.current_user_id() IS DISTINCT FROM OLD.signer_id) THEN
            RAISE EXCEPTION 'Only the person who marked a note sensitive, or its signer, unmarks it'
                USING ERRCODE = '42501', CONSTRAINT = 'clinical_notes_unmark';
        END IF;
        NEW.sensitive_marked_by := NULL;
    END IF;
    NEW.sensitive_changed_at := clock_timestamp();
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.change_note_sensitivity() FROM PUBLIC;
CREATE OR REPLACE TRIGGER clinical_notes_sensitivity
    BEFORE UPDATE ON ehr.clinical_notes
    FOR EACH ROW EXECUTE FUNCTION ehr.change_note_sensitivity();

-- When a note becomes sensitive, so do its addenda: an addendum to a sensitive note is sensitive. With the same
-- reason, as the caller, through the addenda's own rule below. Unmarking the note leaves each addendum as it is.
CREATE OR REPLACE FUNCTION ehr.mark_note_addenda_sensitive() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    UPDATE ehr.note_addenda a SET sensitive = true, sensitive_reason = NEW.sensitive_reason
    WHERE a.note_id = NEW.id AND a.patient_id = NEW.patient_id AND NOT a.sensitive;
    -- All of them, or the note isn't marked: an update the policies hid from this caller would leave one behind.
    IF EXISTS (SELECT 1 FROM ehr.note_addenda a WHERE a.note_id = NEW.id AND NOT a.sensitive) THEN
        RAISE EXCEPTION 'The note''s addenda could not be marked sensitive with it' USING ERRCODE = '42501';
    END IF;
    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.mark_note_addenda_sensitive() FROM PUBLIC;
CREATE OR REPLACE TRIGGER clinical_notes_mark_addenda
    AFTER UPDATE ON ehr.clinical_notes
    FOR EACH ROW WHEN (NEW.sensitive AND NOT OLD.sensitive)
    EXECUTE FUNCTION ehr.mark_note_addenda_sensitive();

-- The desktop's draft sync must hear that a note's sensitivity changed: it withholds the text of a draft signed as a
-- sensitive note, and a desktop that has the text removes it (DraftSyncApi, draft_sync.rs). The draft itself is frozen
-- once signed, so it isn't touched: its row goes into the sync's change log again, as ehr.log_change would put it.
-- Runs as its owner, which alone may write the log (20-change-log.sql).
CREATE OR REPLACE FUNCTION ehr.log_note_sensitivity_change() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    INSERT INTO ehr.change_log (table_name, row_id) VALUES ('clinical_drafts', NEW.draft_id);
    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.log_note_sensitivity_change() FROM PUBLIC;
CREATE OR REPLACE TRIGGER clinical_notes_log_sensitivity
    AFTER UPDATE ON ehr.clinical_notes
    FOR EACH ROW WHEN (NEW.sensitive IS DISTINCT FROM OLD.sensitive)
    EXECUTE FUNCTION ehr.log_note_sensitivity_change();

-- Addenda to signed notes (the expansion plan's step 10). The note stays locked; an addendum is a new signed text
-- attached to it. Before one is inserted, as the caller (row-level security has already required a clinician with the
-- patient on their panel, as themselves): the note must be this patient's and readable by the caller; who signed and
-- when are the database's, never the client's; and an addendum to a sensitive note is sensitive whatever was sent, so
-- it can't be used to put a sensitive note's matter where the chart shows it unasked.
CREATE OR REPLACE FUNCTION ehr.sign_note_addendum() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    note_sensitive boolean;
BEGIN
    -- One at a time for a note, with a mark of it (step 10b; ehr.change_note_sensitivity takes the same lock): an
    -- addendum signed while its note is being marked sensitive waits for the mark, or the mark waits for it and then
    -- marks it too. Without it one could slip between the two and stay unmarked. A lock on the note's ID, not its
    -- row: a row lock would follow the update policy, and a colleague who may add an addendum may not update the note.
    PERFORM pg_advisory_xact_lock(hashtextextended('ehr.clinical_notes:' || NEW.note_id::text, 0));
    SELECT n.sensitive INTO note_sensitive FROM ehr.clinical_notes n WHERE n.id = NEW.note_id AND n.patient_id = NEW.patient_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'The signed note is unavailable' USING ERRCODE = '23514';
    END IF;

    NEW.author_id := ehr.current_user_id();
    NEW.signed_at := clock_timestamp();
    NEW.sensitive := coalesce(NEW.sensitive, false) OR note_sensitive;
    -- Its author is the person who made it sensitive (step 10b), with no reason to give.
    NEW.sensitive_marked_by := CASE WHEN NEW.sensitive THEN ehr.current_user_id() END;
    NEW.sensitive_reason := NULL;
    NEW.sensitive_changed_at := NULL;
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.sign_note_addendum() FROM PUBLIC;
CREATE OR REPLACE TRIGGER note_addenda_sign
    BEFORE INSERT ON ehr.note_addenda
    FOR EACH ROW EXECUTE FUNCTION ehr.sign_note_addendum();

CREATE OR REPLACE FUNCTION ehr.reject_note_addendum_change() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    RAISE EXCEPTION 'Signed addenda are immutable' USING ERRCODE = '23514';
END
$$;
REVOKE ALL ON FUNCTION ehr.reject_note_addendum_change() FROM PUBLIC;
-- A signed addendum is never deleted. Its one update is its sensitivity (below).
CREATE OR REPLACE TRIGGER note_addenda_immutable
    BEFORE DELETE ON ehr.note_addenda
    FOR EACH ROW EXECUTE FUNCTION ehr.reject_note_addendum_change();

-- Marking a signed addendum sensitive, or unmarking it (step 10b): the note's rule, for an addendum. Only the person
-- who made it sensitive, or the signer of the note it belongs to, unmarks it; and not while the note is sensitive,
-- since an addendum to a sensitive note is sensitive. Runs as the caller.
CREATE OR REPLACE FUNCTION ehr.change_addendum_sensitivity() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    apart constant text[] := ARRAY['sensitive', 'sensitive_marked_by', 'sensitive_reason', 'sensitive_changed_at'];
    note_signer uuid;
    note_sensitive boolean;
BEGIN
    IF (to_jsonb(NEW) - apart) IS DISTINCT FROM (to_jsonb(OLD) - apart) THEN
        RAISE EXCEPTION 'Signed addenda are immutable' USING ERRCODE = '23514';
    END IF;
    IF NEW.sensitive IS NOT DISTINCT FROM OLD.sensitive THEN
        RAISE EXCEPTION 'A signed addendum changes only by being marked sensitive or unmarked' USING ERRCODE = '23514';
    END IF;
    NEW.sensitive_reason := btrim(NEW.sensitive_reason);
    IF NEW.sensitive_reason IS NULL OR NEW.sensitive_reason !~ '[^[:space:]]' OR char_length(NEW.sensitive_reason) > 500 THEN
        RAISE EXCEPTION 'Say why, in up to 500 characters' USING ERRCODE = '23514';
    END IF;

    IF NEW.sensitive THEN
        IF ehr.current_user_id() IS NULL THEN
            RAISE EXCEPTION 'An addendum is marked sensitive by someone' USING ERRCODE = '42501';
        END IF;
        NEW.sensitive_marked_by := ehr.current_user_id();
    ELSE
        SELECT n.signer_id, n.sensitive INTO note_signer, note_sensitive FROM ehr.clinical_notes n WHERE n.id = OLD.note_id;
        IF NOT FOUND OR note_sensitive THEN
            RAISE EXCEPTION 'An addendum to a sensitive note is sensitive' USING ERRCODE = '23514';
        END IF;
        IF ehr.current_user_id() IS NULL
           -- One marked before anyone was recorded as marking it was marked by its author.
           OR (ehr.current_user_id() IS DISTINCT FROM coalesce(OLD.sensitive_marked_by, OLD.author_id) AND ehr.current_user_id() IS DISTINCT FROM note_signer) THEN
            RAISE EXCEPTION 'Only the person who marked an addendum sensitive, or the note''s signer, unmarks it'
                USING ERRCODE = '42501', CONSTRAINT = 'note_addenda_unmark';
        END IF;
        NEW.sensitive_marked_by := NULL;
    END IF;
    NEW.sensitive_changed_at := clock_timestamp();
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.change_addendum_sensitivity() FROM PUBLIC;
CREATE OR REPLACE TRIGGER note_addenda_sensitivity
    BEFORE UPDATE ON ehr.note_addenda
    FOR EACH ROW EXECUTE FUNCTION ehr.change_addendum_sensitivity();

-- An addendum written by someone other than the note's signer goes to the signer's inbox (step 10b): "An addendum
-- was added to a note you signed", with the patient and no text. Only while the signer is a clinician who can sign
-- in; otherwise nobody is told, and the addendum is on the note for whoever opens it. Runs as its owner, which alone
-- may add inbox items (10-grants.sql), reading the note through the owner's policy (30-rls.sql).
CREATE OR REPLACE FUNCTION ehr.deliver_addendum() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    INSERT INTO ehr.inbox_items (id, recipient_id, kind, patient_id, source_table, source_id, summary, created_at)
    SELECT uuidv7(), n.signer_id, 'addendum', NEW.patient_id, 'clinical_notes', n.id, 'An addendum was added to a note you signed', clock_timestamp()
    FROM ehr.clinical_notes n
    JOIN ehr.staff_users s ON s.id = n.signer_id
    WHERE n.id = NEW.note_id AND n.patient_id = NEW.patient_id AND n.signer_id <> NEW.author_id
      AND s.roles @> ARRAY['Clinician'] AND (s.lockout_end IS NULL OR s.lockout_end <= now());
    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.deliver_addendum() FROM PUBLIC;
CREATE OR REPLACE TRIGGER note_addenda_deliver
    AFTER INSERT ON ehr.note_addenda
    FOR EACH ROW EXECUTE FUNCTION ehr.deliver_addendum();
