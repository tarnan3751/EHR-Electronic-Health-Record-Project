-- Reviewing emergency access (the expansion plan's step 18; UC 24, proposed FR-64). Applied as ehr_owner after
-- migrations, on every run of infra/migrate; safe to run again. Each emergency access goes to the inbox of every
-- administrator who could review it; one of them, never the clinician who used it, reviews it once it has ended, with
-- an outcome and a note; the review is kept for good and closes every administrator's item. The table's grants are in
-- 10-grants.sql and its row-level security in 30-rls.sql: restricted, like the audit log, so an administrator reads the
-- reviews and adds one as themselves, and nobody else reads them. Administrators read no patient data, the emergency
-- accesses included: ehr.emergency_access_for_review below is their one way to an access's own times.

-- Before a review is added, whoever adds it. Runs as its owner (SECURITY DEFINER), to read the access through the
-- owner's policy on ehr.break_glass_accesses (30-rls.sql), which no administrator has; its search_path is pinned, and
-- nobody but its owner may call it directly. It holds a review to:
--   an administrator who is signed in, whose account isn't locked out (ehr.is_admin; row-level security asks the same);
--   an access that exists (the foreign key's rule too), and has ended, by the database's clock, so the review covers
--     everything done under it;
--   a reviewer who isn't the clinician who used it;
--   a note of 10 to 500 characters once trimmed; and an outcome, which the table's check holds to the two there are;
-- and it sets who reviewed it and when, whatever was sent. Each refusal names a constraint, for the page to say which.
CREATE OR REPLACE FUNCTION ehr.guard_emergency_access_review() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    access record;
BEGIN
    IF NOT ehr.is_admin() THEN
        RAISE EXCEPTION 'Only an administrator reviews an emergency access' USING ERRCODE = '42501';
    END IF;

    SELECT b.clinician_id, b.expires_at INTO access FROM ehr.break_glass_accesses b WHERE b.id = NEW.id;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'There is no such emergency access' USING ERRCODE = '23503', CONSTRAINT = 'emergency_access_reviews_access';
    END IF;
    IF access.expires_at > clock_timestamp() THEN
        RAISE EXCEPTION 'An emergency access is reviewed once it has ended' USING ERRCODE = '23514', CONSTRAINT = 'emergency_access_reviews_ended';
    END IF;
    IF access.clinician_id = ehr.current_user_id() THEN
        RAISE EXCEPTION 'Nobody reviews their own emergency access' USING ERRCODE = '23514', CONSTRAINT = 'emergency_access_reviews_own';
    END IF;

    NEW.note := btrim(NEW.note, E' \t\r\n');
    IF NEW.note IS NULL OR char_length(NEW.note) NOT BETWEEN 10 AND 500 THEN
        RAISE EXCEPTION 'A review''s note is 10 to 500 characters' USING ERRCODE = '23514', CONSTRAINT = 'emergency_access_reviews_note';
    END IF;

    NEW.reviewer_id := ehr.current_user_id();
    NEW.reviewed_at := clock_timestamp();
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.guard_emergency_access_review() FROM PUBLIC;
CREATE OR REPLACE TRIGGER emergency_access_reviews_guard
    BEFORE INSERT ON ehr.emergency_access_reviews
    FOR EACH ROW EXECUTE FUNCTION ehr.guard_emergency_access_review();

-- A review is never changed or deleted, by anyone, the tables' owner included. The runtime roles have neither grant.
CREATE OR REPLACE FUNCTION ehr.reject_emergency_access_review_change() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    RAISE EXCEPTION 'An emergency access review is never changed or deleted' USING ERRCODE = '42501';
END
$$;
REVOKE ALL ON FUNCTION ehr.reject_emergency_access_review_change() FROM PUBLIC;
CREATE OR REPLACE TRIGGER emergency_access_reviews_immutable
    BEFORE UPDATE OR DELETE ON ehr.emergency_access_reviews
    FOR EACH ROW EXECUTE FUNCTION ehr.reject_emergency_access_review_change();

-- Who is told of an emergency access: every administrator whose account isn't disabled (locked out for good, as
-- 80-appointments.sql tells it; one locked for a while after failed sign-ins still is), but the clinician who used it,
-- who may be an administrator too.
CREATE OR REPLACE FUNCTION ehr.emergency_access_reviewers(clinician uuid) RETURNS TABLE (id uuid)
    LANGUAGE sql
    STABLE
    SET search_path = pg_catalog, pg_temp
AS $$
    SELECT s.id FROM ehr.staff_users s
    WHERE s.roles @> ARRAY['Admin'] AND s.id <> clinician
      AND (s.lockout_end IS NULL OR s.lockout_end < now() + interval '10 years')
$$;
REVOKE ALL ON FUNCTION ehr.emergency_access_reviewers(uuid) FROM PUBLIC;

-- An emergency access, once granted, goes to the inbox of each of them: "A clinician used emergency access to a chart",
-- the patient by ID, and the access as its source, which only reviewing it marks done (70-inbox.sql). Runs as its owner
-- (SECURITY DEFINER), which alone may add inbox items (10-grants.sql, 30-rls.sql); its search_path is pinned, and nobody
-- but its owner may call it directly.
-- It never refuses the access: emergency access is patient care, and telling the administrators mustn't stand in its
-- way. With nobody to tell, nobody is told. A failure here is swallowed, and leaves a trace with no patient data in it: an
-- audit event, review_items_failed, naming the access, the clinician and the error's code, nothing else, and the same
-- in the server's log; the sweep at the end of this file makes up the miss at the migration job's next run.
CREATE OR REPLACE FUNCTION ehr.deliver_emergency_access_review() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    BEGIN
        INSERT INTO ehr.inbox_items (id, recipient_id, kind, patient_id, source_table, source_id, summary, created_at)
        SELECT uuidv7(), r.id, 'emergency-access-review', NEW.patient_id, 'break_glass_accesses', NEW.id,
               'A clinician used emergency access to a chart', clock_timestamp()
        FROM ehr.emergency_access_reviewers(NEW.clinician_id) AS r(id);
    EXCEPTION WHEN OTHERS THEN
        BEGIN
            INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, source, sensitive, reason)
            VALUES (uuidv7(), now(), NULLIF(current_setting('app.user_id', true), '')::uuid, 'review_items_failed',
                    'break_glass_accesses', NEW.id, 'onprem', false, SQLSTATE);
        EXCEPTION WHEN OTHERS THEN
            NULL;
        END;
        RAISE WARNING 'Emergency access % wasn''t put in the administrators'' inboxes (SQLSTATE %); the migration job''s next run will.', NEW.id, SQLSTATE;
    END;

    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.deliver_emergency_access_review() FROM PUBLIC;
CREATE OR REPLACE TRIGGER break_glass_accesses_review_items
    AFTER INSERT ON ehr.break_glass_accesses
    FOR EACH ROW EXECUTE FUNCTION ehr.deliver_emergency_access_review();

-- A review marks every administrator's item for its access done, whoever reviewed it. Runs as its owner (SECURITY
-- DEFINER), which alone may mark another's item done and an emergency access's at all (30-rls.sql, 70-inbox.sql); its
-- search_path is pinned, and nobody but its owner may call it directly. The items are found by the access's patient
-- first, which ix_inbox_items_patient_id finds.
CREATE OR REPLACE FUNCTION ehr.close_emergency_access_review_items() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    UPDATE ehr.inbox_items i SET done_at = clock_timestamp()
    WHERE i.patient_id = (SELECT b.patient_id FROM ehr.break_glass_accesses b WHERE b.id = NEW.id)
      AND i.source_table = 'break_glass_accesses' AND i.source_id = NEW.id AND i.done_at IS NULL;
    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.close_emergency_access_review_items() FROM PUBLIC;
CREATE OR REPLACE TRIGGER emergency_access_reviews_close_items
    AFTER INSERT ON ehr.emergency_access_reviews
    FOR EACH ROW EXECUTE FUNCTION ehr.close_emergency_access_review_items();

-- What an administrator reviewing an emergency access sees of it: the patient, by ID, the clinician who used it, the
-- reason they gave, and when it began and ended; the break_glass audit event already shows them all but the end. For an
-- administrator only, the one access asked for; nothing to anyone else. Runs as its owner, through the owner's policy
-- on ehr.break_glass_accesses (30-rls.sql); its search_path is pinned, and only the runtime roles may call it.
CREATE OR REPLACE FUNCTION ehr.emergency_access_for_review(access_id uuid)
    RETURNS TABLE (patient_id uuid, clinician_id uuid, reason text, granted_at timestamptz, expires_at timestamptz)
    LANGUAGE sql
    STABLE
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
    SELECT b.patient_id, b.clinician_id, b.reason::text, b.granted_at, b.expires_at
    FROM ehr.break_glass_accesses b
    WHERE b.id = access_id AND (SELECT ehr.is_admin())
$$;
REVOKE ALL ON FUNCTION ehr.emergency_access_for_review(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ehr.emergency_access_for_review(uuid) TO ehr_app, ehr_read;

-- The sweep, on every run of the migration job: each emergency access not yet reviewed goes to the inbox of every
-- administrator who could review it and hasn't an item for it already. So the accesses from before this step are
-- reviewed too, an administrator added since an access gets it, and an access whose items failed above gets them. The
-- reviews' table is locked against a review being added meanwhile, which would otherwise leave an item no review could
-- close; reviews wait the moment the sweep takes.
BEGIN;
LOCK TABLE ehr.emergency_access_reviews IN SHARE MODE;
INSERT INTO ehr.inbox_items (id, recipient_id, kind, patient_id, source_table, source_id, summary, created_at)
SELECT uuidv7(), r.id, 'emergency-access-review', b.patient_id, 'break_glass_accesses', b.id,
       'A clinician used emergency access to a chart', clock_timestamp()
FROM ehr.break_glass_accesses b
CROSS JOIN LATERAL ehr.emergency_access_reviewers(b.clinician_id) AS r(id)
WHERE NOT EXISTS (SELECT 1 FROM ehr.emergency_access_reviews v WHERE v.id = b.id)
  AND NOT EXISTS (
      SELECT 1 FROM ehr.inbox_items i
      WHERE i.recipient_id = r.id AND i.source_table = 'break_glass_accesses' AND i.source_id = b.id);
COMMIT;
