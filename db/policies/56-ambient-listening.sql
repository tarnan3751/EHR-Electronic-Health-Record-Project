-- Ambient listening (the expansion plan's step 16, proposed FR-62, UC 23): a clinician records a visit's conversation to
-- draft its note, only after confirming in SwiftChart that everyone who would speak agreed aloud. Applied as ehr_owner
-- after migrations, on every run of infra/migrate; safe to run again. The grants are in 10-grants.sql, the row-level
-- security policies in 30-rls.sql, and the write-audit triggers in 40-audit.sql, which stores a transcript's hash and
-- length in place of its text.
--
-- - ehr.ambient_consents: the consent, written before any audio is captured, whose insert the write audit records (who,
--   which patient, when on this database's clock, how many people would speak, who agreed). Never changed or deleted.
-- - ehr.ai_note_reviews: a review of a conversation (mode 'ambient') names its consent, which allows no other review and
--   was given to the same clinician for the same patient at most 10 minutes before. It and its proposals are sensitive
--   items from the start, and its mode, consent and sensitivity never change. Its stored result keeps no word of the
--   conversation, only where each passage was and who said it.
-- - ehr.ambient_transcripts: the conversation's transcript, kept only while its review is open: written as the review
--   is staged, which leaves it pending; deleted in the transaction that finishes or ends the review; and deleted by
--   ehr.purge_ambient_transcripts, as the system's act, 7 days after it was written if nobody has. Once it is gone from a
--   review still open, none of that review's proposals can go into the note as offered: only rejected, or edited into
--   the clinician's own words.

-- The consent: its clinician is the one signed in and its time this database's clock, whatever was sent.
CREATE OR REPLACE FUNCTION ehr.guard_ambient_consent() RETURNS trigger
    LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF TG_OP <> 'INSERT' THEN
        RAISE EXCEPTION 'A consent is a record: it is never changed or deleted' USING ERRCODE = '42501';
    END IF;
    IF NEW.clinician_id IS DISTINCT FROM ehr.current_user_id() THEN
        RAISE EXCEPTION 'A consent is confirmed by the clinician signed in' USING ERRCODE = '42501';
    END IF;
    NEW.confirmed_at := clock_timestamp();
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.guard_ambient_consent() FROM PUBLIC;
CREATE OR REPLACE TRIGGER ambient_consents_guard
    BEFORE INSERT OR UPDATE OR DELETE ON ehr.ambient_consents FOR EACH ROW EXECUTE FUNCTION ehr.guard_ambient_consent();

-- A review of a conversation needs a consent of its own, fresh and given to its clinician for its patient (the foreign
-- key holds the patient); its mode, consent and sensitivity never change. Runs as the caller, whose row-level security
-- shows only their own consents. Beside 55-ai-review.sql's guard, which holds the rest of a review's rules.
-- Its stored result, set once as it is staged (or finished straight from recording), keeps no word of the conversation,
-- which is kept apart in ehr.ambient_transcripts and only while the review is open; it would otherwise stay for good, in
-- the review and in its write's audit event. Exactly what it may hold, as DictationApi.TrustedResult writes it:
--   transcript: {text: "" or none, final, segments: [{start, end, startMs, endMs, speakerGroup: numbers; final: a
--     boolean; unclear: null or a code; speaker: a code}]};
--   sorting (55-ai-review.sql holds it) and reason, a code;
--   fields: null, or the note's fields (and an earlier review's history), each {status: "pending", value, sources: [{start,
--     end, speaker}], warnings: [{code, start, end}]} or {status: "empty", reasons: [codes]};
--   notCarriedOver: [{reason, start, end, speaker: "clinician" or "unsure"}]; proposals: none (a list that is empty, at
--     most);
--   provenance and message.
-- Every place is numbers and every code one of the contract's (DictationApiContract.cs: Speaker, Unclear, WarningCode,
-- Instruction, SortingFailure; an empty field's reasons are the service's, held to the form of a code), so a word of the
-- conversation has nowhere to sit but the three that are text by design: a pending field's value, the words a proposal
-- offers, kept as a dictation's are; provenance, the service's names for its models and code; and message, the app's
-- own sentence for a result that couldn't be sorted. Those are held to their types only.
CREATE OR REPLACE FUNCTION ehr.guard_ambient_review() RETURNS trigger
    LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    consent ehr.ambient_consents%ROWTYPE;
    result jsonb := NEW.result_json;
    fields jsonb := CASE WHEN jsonb_typeof(NEW.result_json -> 'fields') = 'object' THEN NEW.result_json -> 'fields' ELSE '{}' END;
BEGIN
    IF TG_OP = 'UPDATE' THEN
        IF NEW.mode IS DISTINCT FROM OLD.mode OR NEW.consent_id IS DISTINCT FROM OLD.consent_id
           OR NEW.sensitive IS DISTINCT FROM OLD.sensitive THEN
            RAISE EXCEPTION 'A review''s mode, consent and sensitivity never change' USING ERRCODE = '23514';
        END IF;
        IF NEW.mode = 'ambient' AND result IS DISTINCT FROM OLD.result_json AND result IS NOT NULL AND (
            jsonb_typeof(result) <> 'object'
            OR EXISTS (SELECT 1 FROM jsonb_object_keys(result) k
                       WHERE k NOT IN ('transcript', 'sorting', 'reason', 'fields', 'notCarriedOver', 'proposals', 'provenance', 'message'))
            OR jsonb_typeof(result -> 'transcript') IS DISTINCT FROM 'object'
            OR EXISTS (SELECT 1 FROM jsonb_object_keys(result -> 'transcript') k WHERE k NOT IN ('text', 'final', 'segments'))
            OR coalesce(result -> 'transcript' -> 'text', '""') <> '""'
            OR jsonb_typeof(result -> 'transcript' -> 'segments') IS DISTINCT FROM 'array'
            OR EXISTS (SELECT 1 FROM jsonb_array_elements(result -> 'transcript' -> 'segments') x
                       WHERE jsonb_typeof(x) <> 'object' OR EXISTS (SELECT 1 FROM jsonb_each(x) e WHERE NOT (
                           e.key IN ('start', 'end', 'startMs', 'endMs', 'speakerGroup') AND jsonb_typeof(e.value) = 'number'
                           OR e.key = 'final' AND jsonb_typeof(e.value) = 'boolean'
                           OR e.key = 'unclear' AND e.value IN ('null', '"too_quiet"', '"too_noisy"', '"low_confidence"')
                           OR e.key = 'speaker' AND e.value IN ('"clinician"', '"patient"', '"other"', '"unsure"'))))
            OR result -> 'reason' IS NOT NULL AND result -> 'reason' NOT IN ('"model_unavailable"', '"model_timeout"',
                   '"model_output_invalid"', '"transcript_too_long"', '"transcription_failed"')
            OR jsonb_typeof(result -> 'fields') NOT IN ('object', 'null')
            OR EXISTS (SELECT 1 FROM jsonb_each(fields) f
                       WHERE f.key NOT IN ('subjective', 'objective', 'assessment', 'plan', 'history')
                          OR jsonb_typeof(f.value) <> 'object'
                          OR EXISTS (SELECT 1 FROM jsonb_each(f.value) e WHERE NOT (
                               e.key = 'status' AND e.value IN ('"pending"', '"empty"')
                               OR e.key = 'value' AND f.value -> 'status' = '"pending"' AND jsonb_typeof(e.value) = 'string'
                               OR e.key IN ('sources', 'warnings') AND f.value -> 'status' = '"pending"' AND jsonb_typeof(e.value) = 'array'
                               OR e.key = 'reasons' AND f.value -> 'status' = '"empty"' AND jsonb_typeof(e.value) = 'array'
                                  AND NOT EXISTS (SELECT 1 FROM jsonb_array_elements(e.value) r
                                                  WHERE jsonb_typeof(r) <> 'string' OR r #>> '{}' !~ '^[a-z_]{1,64}$')))
                          OR EXISTS (SELECT 1 FROM jsonb_array_elements(CASE WHEN jsonb_typeof(f.value -> 'sources') = 'array' THEN f.value -> 'sources' ELSE '[]' END) x
                                     WHERE jsonb_typeof(x) <> 'object' OR EXISTS (SELECT 1 FROM jsonb_each(x) e WHERE NOT (
                                         e.key IN ('start', 'end') AND jsonb_typeof(e.value) = 'number'
                                         OR e.key = 'speaker' AND e.value IN ('"clinician"', '"patient"', '"other"', '"unsure"'))))
                          OR EXISTS (SELECT 1 FROM jsonb_array_elements(CASE WHEN jsonb_typeof(f.value -> 'warnings') = 'array' THEN f.value -> 'warnings' ELSE '[]' END) x
                                     WHERE jsonb_typeof(x) <> 'object' OR EXISTS (SELECT 1 FROM jsonb_each(x) e WHERE NOT (
                                         e.key IN ('start', 'end') AND jsonb_typeof(e.value) = 'number'
                                         OR e.key = 'code' AND e.value IN ('"possible_correction"', '"near_unclear_passage"',
                                             '"one_field_holds_dictation"', '"speaker_unsure"', '"other_speaker"', '"patients_own_words"',
                                             '"part_not_offered"')))))
            OR result -> 'notCarriedOver' IS NOT NULL AND jsonb_typeof(result -> 'notCarriedOver') <> 'array'
            OR EXISTS (SELECT 1 FROM jsonb_array_elements(CASE WHEN jsonb_typeof(result -> 'notCarriedOver') = 'array' THEN result -> 'notCarriedOver' ELSE '[]' END) x
                       WHERE jsonb_typeof(x) <> 'object' OR EXISTS (SELECT 1 FROM jsonb_each(x) e WHERE NOT (
                           e.key IN ('start', 'end') AND jsonb_typeof(e.value) = 'number'
                           OR e.key = 'reason' AND e.value IN ('"order"', '"allergy_change"')
                           OR e.key = 'speaker' AND e.value IN ('"clinician"', '"unsure"'))))
            OR result -> 'proposals' NOT IN ('null', '[]')
            OR jsonb_typeof(result -> 'provenance') NOT IN ('object', 'null')
            OR jsonb_typeof(result -> 'message') NOT IN ('string', 'null')) THEN
            RAISE EXCEPTION 'A review of a conversation keeps no word of it in its stored result' USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    NEW.sensitive := NEW.mode = 'ambient';
    IF NEW.mode = 'ambient' THEN
        SELECT * INTO consent FROM ehr.ambient_consents c WHERE c.id = NEW.consent_id;
        IF NOT FOUND OR consent.clinician_id <> NEW.clinician_id OR consent.patient_id <> NEW.patient_id
           OR consent.confirmed_at < clock_timestamp() - interval '10 minutes' THEN
            RAISE EXCEPTION 'Listening to a visit needs the consent its clinician just confirmed' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.guard_ambient_review() FROM PUBLIC;
CREATE OR REPLACE TRIGGER ai_note_reviews_ambient
    BEFORE INSERT OR UPDATE ON ehr.ai_note_reviews FOR EACH ROW EXECUTE FUNCTION ehr.guard_ambient_review();

-- A proposal is as sensitive as its review, for good. Once the conversation's transcript of a review still open is gone
-- (ehr.purge_ambient_transcripts), nobody can check a proposal against what was said, so none goes into the note as
-- offered: not accepted, nor edited back to the words offered (the same once trimmed, with every run of whitespace one
-- space), which the review page would otherwise have offered ready to approve.
CREATE OR REPLACE FUNCTION ehr.guard_ambient_proposal() RETURNS trigger
    LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    review ehr.ai_note_reviews%ROWTYPE;
BEGIN
    SELECT * INTO review FROM ehr.ai_note_reviews r WHERE r.id = NEW.review_id;
    IF TG_OP = 'INSERT' THEN
        NEW.sensitive := coalesce(review.sensitive, false);
        RETURN NEW;
    END IF;
    IF NEW.sensitive IS DISTINCT FROM OLD.sensitive THEN
        RAISE EXCEPTION 'A proposal''s sensitivity never changes' USING ERRCODE = '23514';
    END IF;
    IF review.mode = 'ambient' AND NEW.state IS DISTINCT FROM OLD.state
       AND (NEW.state = 'accepted'
            OR NEW.state = 'edited' AND btrim(regexp_replace(NEW.approved_value, '[[:space:]]+', ' ', 'g'))
                                      = btrim(regexp_replace(NEW.original_value, '[[:space:]]+', ' ', 'g')))
       AND NOT EXISTS (SELECT 1 FROM ehr.ambient_transcripts t WHERE t.id = review.id) THEN
        RAISE EXCEPTION 'The conversation''s transcript was deleted: reject this proposal, or edit it into your own words'
            USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.guard_ambient_proposal() FROM PUBLIC;
CREATE OR REPLACE TRIGGER ai_field_proposals_ambient
    BEFORE INSERT OR UPDATE ON ehr.ai_field_proposals FOR EACH ROW EXECUTE FUNCTION ehr.guard_ambient_proposal();

-- The transcript: written only for a review of a conversation as it is staged, never changed, and deleted only once its
-- review is finished or ended, or by the purge below (which runs as ehr_owner, the one role that may delete it while
-- its review is open; the runtime roles can't become it). Written once: in the transaction that stages the review, so
-- never again once the purge has deleted it from a review still open, which would let its proposals be accepted as
-- offered again. That transaction is told as 52-visit-summaries.sql tells it: the review's row as this sees it was
-- written by this transaction, a savepoint of it included (EF Core saves in savepoints), so its xmin is at or after
-- this transaction's own ID, compared as PostgreSQL compares them (modulo 2^32), and still in progress; no other
-- transaction's uncommitted row is visible. A review still pending was staged by an earlier transaction, since any
-- later change to it (55-ai-review.sql) finishes it, and a transaction that has written nothing has no ID.
CREATE OR REPLACE FUNCTION ehr.guard_ambient_transcript() RETURNS trigger
    LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    review ehr.ai_note_reviews%ROWTYPE;
    this bigint := pg_current_xact_id_if_assigned()::text::bigint;
    after bigint;
BEGIN
    IF TG_OP = 'UPDATE' THEN
        RAISE EXCEPTION 'A conversation''s transcript is never changed' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'DELETE' THEN
        IF current_user = 'ehr_owner' THEN
            RETURN OLD;
        END IF;
        SELECT * INTO review FROM ehr.ai_note_reviews r WHERE r.id = OLD.id;
        IF NOT FOUND OR review.state NOT IN ('completed', 'cancelled') THEN
            RAISE EXCEPTION 'A conversation''s transcript is deleted when its review is finished' USING ERRCODE = '23514';
        END IF;
        RETURN OLD;
    END IF;
    SELECT * INTO review FROM ehr.ai_note_reviews r WHERE r.id = NEW.id;
    SELECT (r.xmin::text::bigint - this % 4294967296 + 4294967296) % 4294967296 INTO after
        FROM ehr.ai_note_reviews r WHERE r.id = NEW.id;
    IF NOT FOUND OR review.mode <> 'ambient' OR review.state <> 'pending' OR review.patient_id <> NEW.patient_id
       OR NOT (CASE WHEN this IS NOT NULL AND after < 2147483648
                    THEN pg_xact_status((this + after)::text::xid8) = 'in progress' ELSE false END)
       OR jsonb_typeof(NEW.transcript -> 'text') IS DISTINCT FROM 'string'
       OR jsonb_typeof(NEW.transcript -> 'segments') IS DISTINCT FROM 'array' THEN
        RAISE EXCEPTION 'A transcript is kept only for a review of a conversation as it is staged' USING ERRCODE = '23514';
    END IF;
    NEW.sensitive := true;
    NEW.created_at := clock_timestamp();
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.guard_ambient_transcript() FROM PUBLIC;
CREATE OR REPLACE TRIGGER ambient_transcripts_guard
    BEFORE INSERT OR UPDATE OR DELETE ON ehr.ambient_transcripts FOR EACH ROW EXECUTE FUNCTION ehr.guard_ambient_transcript();

-- At commit, a review of a conversation keeps its transcript exactly while it is open: one staged in this transaction
-- has it, and one finished or ended has none. Run as the caller, who sees their own review and its transcript; at the
-- end of the transaction, so the review's update and the transcript's write or delete may come in either order. A
-- review the caller can't see by then (its user changed before the commit) is refused, never passed: the transcript
-- would be just as unseen.
CREATE OR REPLACE FUNCTION ehr.check_ambient_transcript_kept() RETURNS trigger
    LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    state text;
    kept boolean;
BEGIN
    SELECT r.state INTO state FROM ehr.ai_note_reviews r WHERE r.id = NEW.id;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'A review of a conversation is checked at commit by the clinician who changed it' USING ERRCODE = '42501';
    END IF;
    kept := EXISTS (SELECT 1 FROM ehr.ambient_transcripts t WHERE t.id = NEW.id);
    IF state IN ('completed', 'cancelled') AND kept THEN
        RAISE EXCEPTION 'A finished review of a conversation keeps no transcript' USING ERRCODE = '23514';
    END IF;
    IF OLD.state = 'recording' AND state = 'pending' AND NOT kept THEN
        RAISE EXCEPTION 'A review of a conversation is staged with its transcript' USING ERRCODE = '23514';
    END IF;
    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.check_ambient_transcript_kept() FROM PUBLIC;
DROP TRIGGER IF EXISTS ai_note_reviews_transcript_kept ON ehr.ai_note_reviews;
CREATE CONSTRAINT TRIGGER ai_note_reviews_transcript_kept
    AFTER UPDATE ON ehr.ai_note_reviews DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW WHEN (NEW.mode = 'ambient') EXECUTE FUNCTION ehr.check_ambient_transcript_kept();

-- Deletes the transcripts written before `keep` ago, which are those of reviews nobody finished: a review finished or
-- ended has none. `keep` is held between 1 and 7 days whatever is asked: less only if asked, and never less than a day,
-- so no call deletes every open review's transcript at once. It answers how many it deleted and the time it deleted
-- them before, on this database's clock. The on-site app calls it on a timer with nobody signed in, so the write audit
-- records each delete as the system's act (with the transcript's hash and length): it refuses to run for anyone signed
-- in, which only keeps its caller honest, since the on-site app's role sets app.user_id itself; and it borrows no one's
-- rights. Runs as its owner, the one role whose guard lets it delete transcripts of reviews still open, and whose
-- policies (30-rls.sql) let it read and delete them only while app.ambient_purge is open: this opens it for its delete
-- and clears it before it returns (its transaction, or the savepoint around the call, takes it back when it fails). Its
-- search_path is pinned, and only the on-site app's role may call it.
CREATE OR REPLACE FUNCTION ehr.purge_ambient_transcripts(keep interval)
    RETURNS TABLE (deleted bigint, older_than timestamptz)
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF NULLIF(current_setting('app.user_id', true), '') IS NOT NULL THEN
        RAISE EXCEPTION 'Transcripts are purged by the system, with nobody signed in' USING ERRCODE = '42501';
    END IF;
    older_than := clock_timestamp() - LEAST(GREATEST(coalesce(keep, interval '7 days'), interval '1 day'), interval '7 days');
    PERFORM set_config('app.ambient_purge', 'open', true);
    DELETE FROM ehr.ambient_transcripts t WHERE t.created_at < older_than;
    GET DIAGNOSTICS deleted = ROW_COUNT;
    PERFORM set_config('app.ambient_purge', '', true);
    RETURN NEXT;
END
$$;
REVOKE ALL ON FUNCTION ehr.purge_ambient_transcripts(interval) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ehr.purge_ambient_transcripts(interval) TO ehr_app;
