-- UC 14: signing and edits serialize on the SAME draft row. These invoker functions respect RLS and grants.
-- No runtime UPDATE/DELETE grant on signed notes, with a trigger as an additional immutability boundary.
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
    IF draft.has_unreviewed_proposals THEN
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
CREATE OR REPLACE TRIGGER clinical_notes_immutable
    BEFORE UPDATE OR DELETE ON ehr.clinical_notes
    FOR EACH ROW EXECUTE FUNCTION ehr.reject_clinical_note_change();
