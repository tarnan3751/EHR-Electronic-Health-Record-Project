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
--   items from the start, and its mode, consent and sensitivity never change.
-- - ehr.ambient_transcripts: the conversation's transcript, kept only while its review is open: written as the review
--   is staged, which leaves it pending; deleted in the transaction that finishes or ends the review; and deleted by
--   ehr.purge_ambient_transcripts, as the system's act, 7 days after it was written if nobody has. Once it is gone from a
--   review still open, none of that review's proposals can be accepted as offered: only rejected, or edited into the
--   clinician's own words.

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
CREATE OR REPLACE FUNCTION ehr.guard_ambient_review() RETURNS trigger
    LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    consent ehr.ambient_consents%ROWTYPE;
BEGIN
    IF TG_OP = 'UPDATE' THEN
        IF NEW.mode IS DISTINCT FROM OLD.mode OR NEW.consent_id IS DISTINCT FROM OLD.consent_id
           OR NEW.sensitive IS DISTINCT FROM OLD.sensitive THEN
            RAISE EXCEPTION 'A review''s mode, consent and sensitivity never change' USING ERRCODE = '23514';
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
-- (ehr.purge_ambient_transcripts), nobody can check a proposal against what was said, so none is accepted as offered.
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
    IF NEW.state = 'accepted' AND OLD.state <> 'accepted' AND review.mode = 'ambient'
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
-- its review is open; the runtime roles can't become it).
CREATE OR REPLACE FUNCTION ehr.guard_ambient_transcript() RETURNS trigger
    LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    review ehr.ai_note_reviews%ROWTYPE;
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
    IF NOT FOUND OR review.mode <> 'ambient' OR review.state <> 'pending' OR review.patient_id <> NEW.patient_id
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
-- end of the transaction, so the review's update and the transcript's write or delete may come in either order.
CREATE OR REPLACE FUNCTION ehr.check_ambient_transcript_kept() RETURNS trigger
    LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    state text;
    kept boolean;
BEGIN
    SELECT r.state INTO state FROM ehr.ai_note_reviews r WHERE r.id = NEW.id;
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
