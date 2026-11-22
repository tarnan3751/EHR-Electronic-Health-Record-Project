-- Review mutations lock the SAME draft as signing. Invoker functions retain forced RLS; no privilege escalation.
-- Originals/result provenance never change after staging; decisions are terminal and completion is not a flag toggle.
CREATE OR REPLACE FUNCTION ehr.guard_ai_note_review() RETURNS trigger
    LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    draft ehr.clinical_drafts%ROWTYPE;
    proposed integer;
    decided integer;
BEGIN
    SELECT * INTO draft FROM ehr.clinical_drafts WHERE id = NEW.draft_id FOR UPDATE;
    IF NOT FOUND OR NEW.patient_id <> draft.patient_id OR NEW.clinician_id <> draft.clinician_id THEN
        RAISE EXCEPTION 'Review requires an owned active-panel draft' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF NEW.state <> 'recording' OR NEW.result_json IS NOT NULL OR NEW.imported_version IS NOT NULL
           OR NEW.started_version <> draft.version OR NEW.session_id !~ '^[A-Za-z0-9_-]{16,64}$'
           OR NEW.max_seconds <= 0 OR NEW.max_chunk_bytes <= 0
           OR EXISTS (SELECT 1 FROM ehr.clinical_notes WHERE draft_id = draft.id) THEN
            RAISE EXCEPTION 'Invalid initial dictation binding' USING ERRCODE = '23514';
        END IF;
        NEW.created_at := clock_timestamp();
        NEW.completed_at := NULL;
        RETURN NEW;
    END IF;
    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.patient_id IS DISTINCT FROM OLD.patient_id
       OR NEW.draft_id IS DISTINCT FROM OLD.draft_id OR NEW.clinician_id IS DISTINCT FROM OLD.clinician_id
       OR NEW.session_id IS DISTINCT FROM OLD.session_id OR NEW.created_at IS DISTINCT FROM OLD.created_at
       OR NEW.started_version IS DISTINCT FROM OLD.started_version
       OR NEW.max_seconds IS DISTINCT FROM OLD.max_seconds OR NEW.max_chunk_bytes IS DISTINCT FROM OLD.max_chunk_bytes
       OR OLD.state IN ('completed', 'cancelled') THEN
        RAISE EXCEPTION 'Review identity and completed evidence are immutable' USING ERRCODE = '23514';
    END IF;
    IF OLD.state = 'recording' AND NEW.state = 'cancelled' AND NEW.result_json IS NULL AND NEW.imported_version IS NULL THEN
        RETURN NEW;
    END IF;
    IF EXISTS (SELECT 1 FROM ehr.clinical_notes WHERE draft_id = draft.id) THEN
        RAISE EXCEPTION 'A signed draft cannot receive review changes' USING ERRCODE = '23514';
    END IF;
    IF NEW.result_json IS NULL OR jsonb_typeof(NEW.result_json) <> 'object'
       OR COALESCE(NEW.result_json->>'sorting' NOT IN ('done', 'unavailable'), true) THEN
        RAISE EXCEPTION 'A terminal trusted dictation result is required' USING ERRCODE = '23514';
    END IF;
    SELECT count(*) INTO proposed FROM jsonb_each(CASE WHEN jsonb_typeof(NEW.result_json->'fields') = 'object' THEN NEW.result_json->'fields' ELSE '{}'::jsonb END) f
        WHERE f.value->>'status' = 'pending';
    IF OLD.state = 'recording' AND NEW.state = 'pending' AND proposed > 0 AND NEW.imported_version IS NULL THEN
        NEW.completed_at := NULL;
        RETURN NEW;
    END IF;
    IF OLD.state = 'pending' AND NEW.result_json IS DISTINCT FROM OLD.result_json THEN
        RAISE EXCEPTION 'Staged transcript, sources and originals cannot change' USING ERRCODE = '23514';
    END IF;
    IF NEW.state <> 'completed' OR NEW.imported_version IS DISTINCT FROM draft.version THEN
        RAISE EXCEPTION 'Complete review against the current draft version' USING ERRCODE = '23514';
    END IF;
    SELECT count(*) INTO decided FROM ehr.ai_field_proposals p
        WHERE p.review_id = NEW.id AND p.state IN ('accepted', 'edited', 'rejected');
    IF proposed <> decided OR EXISTS (SELECT 1 FROM ehr.ai_field_proposals p WHERE p.review_id = NEW.id AND p.state = 'pending') THEN
        RAISE EXCEPTION 'Every proposed field requires a recorded decision' USING ERRCODE = '23514';
    END IF;
    NEW.completed_at := clock_timestamp();
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.guard_ai_note_review() FROM PUBLIC;
CREATE OR REPLACE TRIGGER ai_note_reviews_guard
    BEFORE INSERT OR UPDATE ON ehr.ai_note_reviews FOR EACH ROW EXECUTE FUNCTION ehr.guard_ai_note_review();

CREATE OR REPLACE FUNCTION ehr.guard_ai_field_proposal() RETURNS trigger
    LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    review ehr.ai_note_reviews%ROWTYPE;
BEGIN
    SELECT r.* INTO review FROM ehr.ai_note_reviews r JOIN ehr.clinical_drafts d ON d.id = r.draft_id
        WHERE r.id = NEW.review_id FOR UPDATE OF d;
    IF NOT FOUND OR NEW.patient_id <> review.patient_id OR review.state <> 'pending'
       OR EXISTS (SELECT 1 FROM ehr.clinical_notes WHERE draft_id = review.draft_id) THEN
        RAISE EXCEPTION 'Proposal requires an owned pending review' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'INSERT' THEN
        -- The note's four fields (the expansion plan's step 14). A History proposal staged before then is still decided
        -- below, and its approved value goes into Subjective (NoteReviews); no new one is staged.
        IF NEW.field NOT IN ('subjective', 'objective', 'assessment', 'plan') OR NEW.state <> 'pending'
           OR NEW.original_value !~ '[^[:space:]]' OR char_length(NEW.original_value) > 16000
           OR octet_length(NEW.original_value) > 65536
           OR NEW.original_value IS DISTINCT FROM review.result_json #>> ARRAY['fields', NEW.field, 'value']
           OR review.result_json #>> ARRAY['fields', NEW.field, 'status'] IS DISTINCT FROM 'pending'
           OR NEW.approved_value IS NOT NULL OR NEW.decision_key IS NOT NULL
           OR NEW.reviewer_id IS NOT NULL OR NEW.reviewed_at IS NOT NULL THEN
            RAISE EXCEPTION 'Only original pending result fields may be staged' USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.review_id IS DISTINCT FROM OLD.review_id
       OR NEW.patient_id IS DISTINCT FROM OLD.patient_id OR NEW.field IS DISTINCT FROM OLD.field
       OR NEW.original_value IS DISTINCT FROM OLD.original_value OR OLD.state <> 'pending' THEN
        RAISE EXCEPTION 'Original proposals and recorded decisions are immutable' USING ERRCODE = '23514';
    END IF;
    IF NEW.decision_key IS NULL OR NEW.decision_key = '00000000-0000-0000-0000-000000000000'::uuid
       OR NEW.state NOT IN ('accepted', 'edited', 'rejected')
       OR (NEW.state = 'accepted' AND NEW.approved_value IS DISTINCT FROM NEW.original_value)
       OR (NEW.state = 'rejected' AND NEW.approved_value IS NOT NULL)
       OR (NEW.state = 'edited' AND (NEW.approved_value IS NULL OR NEW.approved_value !~ '[^[:space:]]'
            OR char_length(NEW.approved_value) > 16000 OR octet_length(NEW.approved_value) > 65536)) THEN
        RAISE EXCEPTION 'Record an explicit valid accept, edit or reject decision' USING ERRCODE = '23514';
    END IF;
    NEW.reviewer_id := ehr.current_user_id();
    NEW.reviewed_at := clock_timestamp();
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.guard_ai_field_proposal() FROM PUBLIC;
CREATE OR REPLACE TRIGGER ai_field_proposals_guard
    BEFORE INSERT OR UPDATE ON ehr.ai_field_proposals FOR EACH ROW EXECUTE FUNCTION ehr.guard_ai_field_proposal();
