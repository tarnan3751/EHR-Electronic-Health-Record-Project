-- Secure messages (FR-18; the expansion plan's step 5). Applied as ehr_owner after migrations, on every run of
-- infra/migrate; safe to re-run. The grants are in 10-grants.sql and the row-level security policies in 30-rls.sql.

-- Before a thread is inserted: the database sets when, whatever the client sent. And a patient starts at most 10
-- conversations in a day: each one puts an item in every care-team clinician's inbox, so without a limit one account
-- could bury a care team. The error is program_limit_exceeded (54000), which the portal turns into "call the
-- clinic"; a 53xxx code would be retried as a transient failure. Staff have no limit, and a conversation staff
-- started doesn't count. Runs as the caller, who sees only their own threads.
CREATE OR REPLACE FUNCTION ehr.start_message_thread() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF ehr.current_patient_id() IS NOT NULL
       AND (SELECT count(*) FROM ehr.message_threads t
            WHERE t.patient_id = NEW.patient_id AND t.started_at > now() - interval '1 day'
              -- Not the ones staff started: those whose first message is from staff.
              AND (SELECT m.sender_kind FROM ehr.messages m WHERE m.thread_id = t.id ORDER BY m.sent_at, m.id LIMIT 1)
                  IS DISTINCT FROM 'staff') >= 10 THEN
        RAISE EXCEPTION 'A patient starts at most 10 conversations a day' USING ERRCODE = '54000';
    END IF;

    NEW.started_at := clock_timestamp();
    NEW.last_message_at := NEW.started_at;
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.start_message_thread() FROM PUBLIC;
CREATE OR REPLACE TRIGGER message_threads_start
    BEFORE INSERT ON ehr.message_threads
    FOR EACH ROW EXECUTE FUNCTION ehr.start_message_thread();

-- Before a message is inserted or updated. Runs as the caller, so the grants and row-level security bind it.
--   insert: the message belongs to its thread's patient; the sender is the signed-in account and the time the
--           database's, whatever the client sent; it starts unread.
--   update: the one change is being read, once, at the database's time. No other column moves, and nothing un-reads.
CREATE OR REPLACE FUNCTION ehr.guard_message() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        PERFORM 1 FROM ehr.message_threads t WHERE t.id = NEW.thread_id AND t.patient_id = NEW.patient_id;
        IF NOT FOUND THEN
            RAISE EXCEPTION 'A message belongs to its thread''s patient' USING ERRCODE = '23514';
        END IF;

        IF coalesce(NEW.body, '') !~ '[^[:space:]]' THEN
            RAISE EXCEPTION 'A message says something' USING ERRCODE = '23514';
        END IF;

        -- A patient sends at most 30 messages in an hour, for the reason they start at most 10 conversations a day.
        IF NEW.sender_kind = 'patient'
           AND (SELECT count(*) FROM ehr.messages m
                WHERE m.patient_id = NEW.patient_id AND m.sender_kind = 'patient' AND m.sent_at > now() - interval '1 hour') >= 30 THEN
            RAISE EXCEPTION 'A patient sends at most 30 messages an hour' USING ERRCODE = '54000';
        END IF;

        NEW.sender_id := ehr.current_user_id();
        NEW.sent_at := clock_timestamp();
        NEW.read_at := NULL;
        RETURN NEW;
    END IF;

    IF OLD.read_at IS NOT NULL OR NEW.read_at IS NULL THEN
        RAISE EXCEPTION 'A message is marked read once' USING ERRCODE = '23514';
    END IF;

    IF (to_jsonb(NEW) - 'read_at') IS DISTINCT FROM (to_jsonb(OLD) - 'read_at') THEN
        RAISE EXCEPTION 'A message is never edited' USING ERRCODE = '42501';
    END IF;

    NEW.read_at := clock_timestamp();
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.guard_message() FROM PUBLIC;
CREATE OR REPLACE TRIGGER messages_guard
    BEFORE INSERT OR UPDATE ON ehr.messages
    FOR EACH ROW EXECUTE FUNCTION ehr.guard_message();

-- After a message is inserted: what follows from it, which the sender has no rights to do themselves. Runs as its
-- owner, ehr_owner (SECURITY DEFINER), with its search_path pinned; nobody but its owner may call it directly.
--   1. The thread's latest-message time moves on.
--   2. A patient's message goes into the inbox of each clinician with the patient on their active panel: their care
--      team. The item's summary says only that a patient sent a message. A clinician who already has an open item for
--      the thread doesn't get a second.
--   3. A staff message answers the thread: its open inbox items are marked done, for the whole care team. That
--      includes an item for a patient message that arrived a moment before the reply; the page the reply returns to
--      shows that message, and the patient's next one opens a new item.
CREATE OR REPLACE FUNCTION ehr.deliver_message() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    UPDATE ehr.message_threads SET last_message_at = NEW.sent_at WHERE id = NEW.thread_id;

    IF NEW.sender_kind = 'patient' THEN
        INSERT INTO ehr.inbox_items (id, recipient_id, kind, patient_id, source_table, source_id, summary, created_at)
        SELECT uuidv7(), a.clinician_id, 'message', NEW.patient_id, 'message_threads', NEW.thread_id, 'A patient sent a message', NEW.sent_at
        FROM ehr.patient_assignments a
        WHERE a.patient_id = NEW.patient_id AND a.ended_at IS NULL
          AND NOT EXISTS (
              SELECT 1 FROM ehr.inbox_items i
              WHERE i.recipient_id = a.clinician_id AND i.source_table = 'message_threads' AND i.source_id = NEW.thread_id AND i.done_at IS NULL);
    ELSE
        UPDATE ehr.inbox_items SET done_at = clock_timestamp()
        WHERE source_table = 'message_threads' AND source_id = NEW.thread_id AND done_at IS NULL;
    END IF;

    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.deliver_message() FROM PUBLIC;
CREATE OR REPLACE TRIGGER messages_deliver
    AFTER INSERT ON ehr.messages
    FOR EACH ROW EXECUTE FUNCTION ehr.deliver_message();
