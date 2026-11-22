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

-- The orders and allergies to add a dictation proposes (the expansion plan's step 15; FR-14 as reworded). Before each
-- insert or update, by whoever makes it. Runs as the caller, so the grants and forced row-level security bind it: only
-- the review's own clinician sees the review, and so its proposals.
--   Insert, as a review is staged: the proposal at its place in the review's stored result, of the kind the result
--     gives there, waiting; never once the review's draft is signed.
--   Update: decided once; nothing else about it moves, and who and when are the database's (the column grants keep the
--     apps to the decision's columns; this holds the tables' owner to them too).
--     Ordered, by naming the order signed for it: an active order of the patient, signed now by the clinician deciding,
--       not a change of another order, and not one that answers a refill request (D7: an order answers a refill request
--       or decides a proposal, never both; 77-refill-requests.sql holds the other way). Signing it went through every
--       rule and check of 60-orders.sql and 61-order-safety.sql, as any order's does: this only links it. "Now" is
--       signed_at >= now(), as 77-refill-requests.sql tells it: since this transaction began, which is the order this
--       transaction signed, or one the same clinician signed for the same patient in another transaction of theirs
--       committed since this one began; never one signed before it. Letting the second through is safe: it is that
--       clinician's own order for that patient, through every check, and the app links only the order it has just
--       signed, in the same savepoint. In a READ COMMITTED transaction, as signing an order requires: before it looks
--       for a refill request naming the order, it takes the order's advisory lock ('ehr.orders:' and its ID), as
--       approving a refill request does before it looks for a proposal naming it, so of two transactions linking one
--       order the second waits for the first and then sees what it wrote. The app takes it last (Lock order in
--       77-refill-requests.sql): it links only the order it has just signed, and waits for no other lock while holding
--       it. A transaction written otherwise can deadlock on it: one that links an order and then signs another for the
--       same patient (which locks the patient's row) against one that signs and then approves a refill request with
--       that order. PostgreSQL then aborts one of them (40P01), and no order is linked wrongly: the other finds the
--       aborted one's link gone and goes on as if it had come first.
--     Recorded, by naming the allergy added for it: an active, coded allergy of the patient, not "No known allergies",
--       added now by the clinician deciding, as its first row in ehr.chart_history says (written only by
--       65-chart-lists.sql's trigger, as its owner): one that was there before and only changed now isn't. "Now" is
--       changed_at >= now(), as for an order: added in this transaction, or by the same clinician for the same patient
--       in another of their transactions committed since this one began, which is as safe, for the same reasons.
--     Dismissed.
--   The table's checks hold the rest: waiting with no decision and nothing named, and a decision with who and when (so
--   by someone signed in); an order's proposal ordered with its order alone, an allergy's recorded with its allergy
--   alone, a dismissed one naming neither. An order, or an allergy, decides one proposal (unique indexes). The note may
--   be signed meanwhile: a proposal waits for its decision whatever becomes of the note, and blocks nothing
--   (50-clinical-notes.sql never reads it).
CREATE OR REPLACE FUNCTION ehr.guard_ai_chart_proposal() RETURNS trigger
    LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    review ehr.ai_note_reviews%ROWTYPE;
BEGIN
    -- Not found reads as all nulls, refused here too.
    SELECT r.* INTO review FROM ehr.ai_note_reviews r WHERE r.id = NEW.review_id;
    IF review.patient_id IS DISTINCT FROM NEW.patient_id THEN
        RAISE EXCEPTION 'A proposal belongs to a dictation review of yours'
            USING ERRCODE = '23514', CONSTRAINT = 'ai_chart_proposals_review';
    END IF;

    IF TG_OP = 'INSERT' THEN
        -- A review with no stored result yet has no kind at any place.
        IF NEW.kind IS DISTINCT FROM review.result_json #>> ARRAY['proposals', NEW.ordinal::text, 'kind']
           OR NEW.state IS DISTINCT FROM 'pending'
           OR EXISTS (SELECT 1 FROM ehr.clinical_notes n WHERE n.draft_id = review.draft_id) THEN
            RAISE EXCEPTION 'Only a waiting proposal its review''s stored result holds is staged'
                USING ERRCODE = '23514', CONSTRAINT = 'ai_chart_proposals_staged';
        END IF;
        RETURN NEW;
    END IF;

    -- Its patient is held to its review's above.
    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.review_id IS DISTINCT FROM OLD.review_id
       OR NEW.ordinal IS DISTINCT FROM OLD.ordinal OR NEW.kind IS DISTINCT FROM OLD.kind THEN
        RAISE EXCEPTION 'A proposal stays what its dictation gave' USING ERRCODE = '42501';
    END IF;
    IF OLD.state IS DISTINCT FROM 'pending' THEN
        RAISE EXCEPTION 'A proposal is decided once' USING ERRCODE = '23514', CONSTRAINT = 'ai_chart_proposals_decided';
    END IF;
    IF NEW.state = 'ordered' THEN
        IF current_setting('transaction_isolation') <> 'read committed' THEN
            RAISE EXCEPTION 'A proposed order is decided in a READ COMMITTED transaction' USING ERRCODE = '0A000';
        END IF;
        PERFORM pg_advisory_xact_lock(hashtextextended('ehr.orders:' || NEW.order_id::text, 0));
        IF NOT EXISTS (
            SELECT 1 FROM ehr.orders o
            WHERE o.id = NEW.order_id AND o.patient_id = NEW.patient_id AND o.status = 'active'
              AND o.signer_id = ehr.current_user_id() AND o.signed_at >= now() AND o.replaces_order_id IS NULL) THEN
            RAISE EXCEPTION 'A proposed order is decided by the order signed for it, now, by whoever decides it'
                USING ERRCODE = '23514', CONSTRAINT = 'ai_chart_proposals_order';
        END IF;
        IF EXISTS (SELECT 1 FROM ehr.refill_requests q WHERE q.order_id = NEW.order_id) THEN
            RAISE EXCEPTION 'An order that answers a refill request decides no proposal'
                USING ERRCODE = '23514', CONSTRAINT = 'ai_chart_proposals_order_refill';
        END IF;
    END IF;
    -- An allergy with no code isn't one whose code is not "No known allergies". Its history is found by the index on the
    -- patient, the table and the time.
    IF NEW.state = 'recorded' AND NOT EXISTS (
        SELECT 1 FROM ehr.allergy_intolerances a
        JOIN ehr.chart_history h ON h.patient_id = a.patient_id AND h.table_name = 'allergy_intolerances' AND h.row_id = a.id
        WHERE a.id = NEW.allergy_id AND a.patient_id = NEW.patient_id AND a.clinical_status = 'active'
          AND a.code_system IS NOT NULL AND a.code <> 'no-known-allergy'
          AND h.changed_at >= now() AND h.before IS NULL AND h.changed_by_id = ehr.current_user_id()) THEN
        RAISE EXCEPTION 'A proposed allergy is decided by the coded allergy added for it, now, by whoever decides it'
            USING ERRCODE = '23514', CONSTRAINT = 'ai_chart_proposals_allergy';
    END IF;

    NEW.decided_by := ehr.current_user_id();
    NEW.decided_at := clock_timestamp();
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.guard_ai_chart_proposal() FROM PUBLIC;
CREATE OR REPLACE TRIGGER ai_chart_proposals_guard
    BEFORE INSERT OR UPDATE ON ehr.ai_chart_proposals FOR EACH ROW EXECUTE FUNCTION ehr.guard_ai_chart_proposal();
