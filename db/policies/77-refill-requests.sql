-- Refill requests (the expansion plan's step 13d): the patient, or someone they share their record with whose access
-- includes messages, asks the care team in the portal to renew a medication on the chart; one of the care team approves
-- it by signing an order for the same medication product through the order form, or declines it with a message for the
-- patient.
-- What the database itself holds them to, whatever the app does. Who may ask, read and answer is row-level security's
-- (30-rls.sql), but for the portal, which reads them only through ehr.portal_refill_requests below; the grants are in
-- 10-grants.sql. Plain SQL, safe to run again.

-- Before a request is inserted or updated, whoever makes the change, the tables' owner included. Runs as the caller.
--   Insert, as row-level security has already required a portal session asking for the record it is looking at, under
--     access that includes messages, about a medication of that record it may read (30-rls.sql):
--     one at a time with the medication's sensitivity changing, under the medication's lock (the one
--     ehr.follow_refill_medication takes), so the mark copied here is the one that stands; the medication must be
--     active and coded as a product the clinic can order (its RxNorm code an item of the order catalog: one coded as an
--     ingredient, or not coded, is not renewed here); the note, trimmed, is up to 500 characters or none; who asked and
--     when are the database's, and so are the medication's name and sensitivity; it starts waiting, with no answer.
--   Update by the tables' owner: only the sensitivity, which ehr.follow_refill_medication keeps the medication's.
--   Update by the apps: the one answer, once, by someone signed in, and nothing else moves. Its sensitivity is never
--     theirs to change.
--     Approved, by naming the order signed for it: an active medication order of this patient, signed in this
--       transaction by the clinician answering, for the same product as the medication on the chart (the order's catalog
--       item is the medication's own RxNorm code: another strength, form, salt or route is a new order, not a renewal);
--       the medication still active, and locked until the transaction ends, so stopping it waits for the approval or the
--       approval sees it stopped. The database copies what was ordered.
--     Declined, with a message for the patient of up to 1,000 characters.
--     Who answered and when are the database's.
-- READ COMMITTED, as signing an order requires (60-orders.sql): a statement that waited for a lock reads what was
-- committed while it waited.
CREATE OR REPLACE FUNCTION ehr.guard_refill_request() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    medication record;
    ordered record;
    answer constant text[] := ARRAY['status', 'order_id', 'ordered_display', 'decline_message', 'decided_by', 'decided_at'];
BEGIN
    IF TG_OP = 'UPDATE' AND current_user NOT IN ('ehr_app', 'ehr_read') THEN
        IF (to_jsonb(NEW) - 'sensitive') IS DISTINCT FROM (to_jsonb(OLD) - 'sensitive') THEN
            RAISE EXCEPTION 'A refill request changes only by its answer' USING ERRCODE = '42501';
        END IF;
        RETURN NEW;
    END IF;

    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'A refill request is asked for and answered in a READ COMMITTED transaction' USING ERRCODE = '0A000';
    END IF;

    IF ehr.current_user_id() IS NULL THEN
        RAISE EXCEPTION 'A refill request is asked for and answered by someone' USING ERRCODE = '42501';
    END IF;

    IF TG_OP = 'INSERT' THEN
        PERFORM pg_advisory_xact_lock(hashtextextended('ehr.medication_statements:' || NEW.medication_statement_id::text, 0));
        SELECT m.display, m.status, m.code, m.sensitive INTO medication
        FROM ehr.medication_statements m
        WHERE m.id = NEW.medication_statement_id AND m.patient_id = NEW.patient_id;
        IF NOT FOUND THEN
            RAISE EXCEPTION 'That medication isn''t one this record may ask to renew'
                USING ERRCODE = '42501', CONSTRAINT = 'refill_requests_medication';
        END IF;
        IF medication.status IS DISTINCT FROM 'active' THEN
            RAISE EXCEPTION 'Only an active medication is renewed' USING ERRCODE = '23514', CONSTRAINT = 'refill_requests_medication_active';
        END IF;
        IF NOT EXISTS (SELECT 1 FROM ehr.order_catalog_items i WHERE i.code = medication.code AND i.type = 'medication') THEN
            RAISE EXCEPTION 'A medication not coded as a product the clinic orders can''t be renewed here'
                USING ERRCODE = '23514', CONSTRAINT = 'refill_requests_medication_coded';
        END IF;

        NEW.note := NULLIF(btrim(NEW.note, E' \t\r\n'), '');
        IF char_length(NEW.note) > 500 THEN
            RAISE EXCEPTION 'A note is up to 500 characters' USING ERRCODE = '23514', CONSTRAINT = 'refill_requests_note';
        END IF;
        NEW.medication_display := medication.display;
        NEW.sensitive := medication.sensitive;
        NEW.requested_by := ehr.current_user_id();
        NEW.requested_at := clock_timestamp();
        NEW.status := 'requested';
        NEW.order_id := NULL;
        NEW.ordered_display := NULL;
        NEW.decline_message := NULL;
        NEW.decided_by := NULL;
        NEW.decided_at := NULL;
        RETURN NEW;
    END IF;

    IF NEW.sensitive IS DISTINCT FROM OLD.sensitive THEN
        RAISE EXCEPTION 'A refill request is sensitive when its medication is' USING ERRCODE = '42501';
    END IF;
    IF OLD.status IS DISTINCT FROM 'requested' THEN
        RAISE EXCEPTION 'A refill request is answered once' USING ERRCODE = '23514', CONSTRAINT = 'refill_requests_answered';
    END IF;
    IF (to_jsonb(NEW) - answer) IS DISTINCT FROM (to_jsonb(OLD) - answer) THEN
        RAISE EXCEPTION 'A refill request changes only by its answer' USING ERRCODE = '42501';
    END IF;
    IF (NEW.order_id IS NULL) = (NEW.decline_message IS NULL) THEN
        RAISE EXCEPTION 'A refill request is answered by an order or by a message, one of the two' USING ERRCODE = '23514';
    END IF;

    IF NEW.order_id IS NOT NULL THEN
        SELECT m.code INTO medication
        FROM ehr.medication_statements m
        WHERE m.id = OLD.medication_statement_id AND m.patient_id = OLD.patient_id AND m.status = 'active'
        FOR SHARE;
        IF NOT FOUND THEN
            RAISE EXCEPTION 'Only an active medication is renewed' USING ERRCODE = '23514', CONSTRAINT = 'refill_requests_medication_active';
        END IF;

        SELECT i.display, o.catalog_code INTO ordered
        FROM ehr.orders o
        JOIN ehr.order_catalog_items i ON i.code = o.catalog_code
        WHERE o.id = NEW.order_id AND o.patient_id = OLD.patient_id AND o.type = 'medication' AND o.status = 'active'
          AND o.signer_id = ehr.current_user_id() AND o.signed_at >= now();
        IF NOT FOUND THEN
            RAISE EXCEPTION 'A refill request is approved by the medication order signed for it, now, by whoever approves it'
                USING ERRCODE = '23514', CONSTRAINT = 'refill_requests_order';
        END IF;
        IF ordered.catalog_code IS DISTINCT FROM medication.code THEN
            RAISE EXCEPTION 'A refill renews the same medication product' USING ERRCODE = '23514', CONSTRAINT = 'refill_requests_same_medication';
        END IF;

        NEW.status := 'completed';
        NEW.ordered_display := ordered.display;
    ELSE
        NEW.decline_message := btrim(NEW.decline_message, E' \t\r\n');
        IF NEW.decline_message !~ '[^[:space:]]' OR char_length(NEW.decline_message) > 1000 THEN
            RAISE EXCEPTION 'Declining a refill request needs a message for the patient, of up to 1,000 characters'
                USING ERRCODE = '23514', CONSTRAINT = 'refill_requests_decline_message';
        END IF;
        NEW.status := 'rejected';
        NEW.ordered_display := NULL;
    END IF;

    NEW.decided_by := ehr.current_user_id();
    NEW.decided_at := clock_timestamp();
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.guard_refill_request() FROM PUBLIC;
CREATE OR REPLACE TRIGGER refill_requests_guard
    BEFORE INSERT OR UPDATE ON ehr.refill_requests
    FOR EACH ROW EXECUTE FUNCTION ehr.guard_refill_request();

-- A request is never deleted, by anyone: answered or waiting, it stays in the record.
CREATE OR REPLACE FUNCTION ehr.reject_refill_request_delete() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    RAISE EXCEPTION 'A refill request is never deleted' USING ERRCODE = '23514';
END
$$;
REVOKE ALL ON FUNCTION ehr.reject_refill_request_delete() FROM PUBLIC;
CREATE OR REPLACE TRIGGER refill_requests_immutable
    BEFORE DELETE ON ehr.refill_requests
    FOR EACH ROW EXECUTE FUNCTION ehr.reject_refill_request_delete();

-- What follows from a request, which the person asking or answering has no rights to do themselves. Runs as its owner,
-- ehr_owner (SECURITY DEFINER), which alone may add inbox items (10-grants.sql), with its search_path pinned; nobody but
-- its owner may call it directly.
--   Asked: an inbox item for each clinician with the patient on their active panel whose account isn't disabled, the
--     care team, saying only that a patient asked to renew a medication. An account locked for a while after failed
--     sign-ins is on the team still; one an admin disabled (locked out for good, 80-appointments.sql's test) is not.
--     With none, the request is refused: nobody would be told of it, and the portal says to call the clinic.
--   Answered: every item still open for it is done, for the whole care team, whoever answered.
CREATE OR REPLACE FUNCTION ehr.deliver_refill_request() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    delivered integer;
BEGIN
    IF TG_OP = 'INSERT' THEN
        INSERT INTO ehr.inbox_items (id, recipient_id, kind, patient_id, source_table, source_id, summary, created_at)
        SELECT uuidv7(), s.id, 'refill-request', NEW.patient_id, 'refill_requests', NEW.id, 'A patient asked to renew a medication', NEW.requested_at
        FROM ehr.patient_assignments a
        JOIN ehr.staff_users s ON s.id = a.clinician_id
        WHERE a.patient_id = NEW.patient_id AND a.ended_at IS NULL
          AND s.roles @> ARRAY['Clinician'] AND (s.lockout_end IS NULL OR s.lockout_end < now() + interval '10 years');
        GET DIAGNOSTICS delivered = ROW_COUNT;
        IF delivered = 0 THEN
            RAISE EXCEPTION 'This patient has no care team to answer a refill request'
                USING ERRCODE = '23514', CONSTRAINT = 'refill_requests_care_team';
        END IF;
    ELSE
        UPDATE ehr.inbox_items SET done_at = clock_timestamp()
        WHERE source_table = 'refill_requests' AND source_id = NEW.id AND done_at IS NULL;
    END IF;

    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.deliver_refill_request() FROM PUBLIC;
CREATE OR REPLACE TRIGGER refill_requests_deliver
    AFTER INSERT ON ehr.refill_requests
    FOR EACH ROW EXECUTE FUNCTION ehr.deliver_refill_request();
CREATE OR REPLACE TRIGGER refill_requests_answered
    AFTER UPDATE ON ehr.refill_requests
    FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
    EXECUTE FUNCTION ehr.deliver_refill_request();

-- A waiting request follows the care team, so it never sits where nobody can answer it. When a clinician joins a
-- patient's panel, they get an item for each of that patient's waiting requests, but one they hold open already; when
-- their assignment ends, their open refill items for that patient are done, since they can no longer answer them. A
-- request whose patient is left with nobody on the panel stays waiting, and goes to whoever joins next. The test of who
-- is on the team is the one asking uses. Runs as its owner (SECURITY DEFINER), which alone may add inbox items and close
-- refill items (10-grants.sql, 70-inbox.sql); its search_path is pinned, and nobody but its owner may call it directly.
-- It reads the requests and the staff account, and writes the inbox: nothing else.
CREATE OR REPLACE FUNCTION ehr.follow_refill_care_team() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        INSERT INTO ehr.inbox_items (id, recipient_id, kind, patient_id, source_table, source_id, summary, created_at)
        SELECT uuidv7(), s.id, 'refill-request', r.patient_id, 'refill_requests', r.id, 'A patient asked to renew a medication', clock_timestamp()
        FROM ehr.refill_requests r
        JOIN ehr.staff_users s ON s.id = NEW.clinician_id
        WHERE r.patient_id = NEW.patient_id AND r.status = 'requested'
          AND s.roles @> ARRAY['Clinician'] AND (s.lockout_end IS NULL OR s.lockout_end < now() + interval '10 years')
          AND NOT EXISTS (
              SELECT 1 FROM ehr.inbox_items i
              WHERE i.recipient_id = NEW.clinician_id AND i.source_table = 'refill_requests' AND i.source_id = r.id AND i.done_at IS NULL);
    ELSE
        UPDATE ehr.inbox_items SET done_at = clock_timestamp()
        WHERE recipient_id = OLD.clinician_id AND patient_id = OLD.patient_id AND kind = 'refill-request' AND source_table = 'refill_requests'
          AND done_at IS NULL;
    END IF;

    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.follow_refill_care_team() FROM PUBLIC;
CREATE OR REPLACE TRIGGER patient_assignments_join_refills
    AFTER INSERT ON ehr.patient_assignments
    FOR EACH ROW WHEN (NEW.ended_at IS NULL)
    EXECUTE FUNCTION ehr.follow_refill_care_team();
CREATE OR REPLACE TRIGGER patient_assignments_leave_refills
    AFTER UPDATE ON ehr.patient_assignments
    FOR EACH ROW WHEN (OLD.ended_at IS NULL AND NEW.ended_at IS NOT NULL)
    EXECUTE FUNCTION ehr.follow_refill_care_team();

-- A request is sensitive when its medication is: when a medication is marked sensitive, or unmarked, its requests
-- follow, waiting or answered, so someone the patient shares their record with but not sensitive items loses them at
-- once, and the write audit marks their earlier events (40-audit.sql). Under the medication's lock, which a request
-- being asked for takes too, so a request asked for at the same moment is either seen here or copies the new mark. Runs
-- as its owner (SECURITY DEFINER), which alone may change a request's sensitivity (ehr.guard_refill_request, and the
-- owner's policies in 30-rls.sql); its search_path is pinned, and nobody but its owner may call it directly. It reads and
-- writes nothing but the requests.
CREATE OR REPLACE FUNCTION ehr.follow_refill_medication() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(hashtextextended('ehr.medication_statements:' || NEW.id::text, 0));
    UPDATE ehr.refill_requests SET sensitive = NEW.sensitive
    WHERE medication_statement_id = NEW.id AND sensitive IS DISTINCT FROM NEW.sensitive;
    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.follow_refill_medication() FROM PUBLIC;
CREATE OR REPLACE TRIGGER medication_statements_follow_refills
    AFTER UPDATE ON ehr.medication_statements
    FOR EACH ROW WHEN (NEW.sensitive IS DISTINCT FROM OLD.sensitive)
    EXECUTE FUNCTION ehr.follow_refill_medication();

-- The portal's refill requests, for the record the session is looking at: the latest first, up to max_rows, each with
-- the medication it was asked for (its entry on the chart and its name then), the note, whether the reader asked it
-- ('you') or, for the patient themselves, someone they share their record with ('proxy'), when, its state, what was
-- ordered or the care team's message, when it was answered, and whether it is sensitive; and nothing else of it: not
-- who answered it, the order, or which account asked. The patient's own, and a proxy's only while their access is in
-- force and includes messages; a sensitive one only where ehr.portal_sees_sensitive says so: always the patient's own, a
-- proxy's only when the patient allowed sensitive items. Row-level security can't keep some of a row's columns from a
-- session that may read the row, so no portal session reads the table (30-rls.sql): this is the portal's only way to a
-- request, as ehr.standing_visit_summaries is to a summary. Runs as its owner, reading the requests through the owner's
-- policy (30-rls.sql); its search_path is pinned, and only the runtime roles may call it. Anyone who isn't a portal
-- session looking at a record gets nothing.
CREATE OR REPLACE FUNCTION ehr.portal_refill_requests(max_rows integer)
    RETURNS TABLE (medication_statement_id uuid, medication_display text, note text, asked text, requested_at timestamptz, status text,
                   ordered_display text, decline_message text, decided_at timestamptz, sensitive boolean)
    LANGUAGE sql
    STABLE
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
    SELECT r.medication_statement_id, r.medication_display::text, r.note::text,
           CASE WHEN r.requested_by = (SELECT ehr.current_user_id()) THEN 'you'
                WHEN (SELECT ehr.acting_grant_id()) IS NULL THEN 'proxy' END,
           r.requested_at, r.status::text, r.ordered_display::text, r.decline_message::text, r.decided_at, r.sensitive
    FROM ehr.refill_requests r
    WHERE r.patient_id = (SELECT ehr.current_patient_id()) AND (SELECT ehr.portal_may_message())
      AND (NOT r.sensitive OR (SELECT ehr.portal_sees_sensitive()))
    ORDER BY r.requested_at DESC, r.id
    LIMIT max_rows
$$;
REVOKE ALL ON FUNCTION ehr.portal_refill_requests(integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ehr.portal_refill_requests(integer) TO ehr_app, ehr_read;
