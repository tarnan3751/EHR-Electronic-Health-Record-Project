-- Pre-visit forms (the expansion plan's step 13e): a clinician asks a patient to fill in a questionnaire before a booked
-- appointment, and the patient, or a proxy the patient allowed sensitive items, answers it once in the portal. Applied
-- as ehr_owner after migrations, on every run of infra/migrate; safe to run again. The grants are in 10-grants.sql and
-- the row-level security policies in 30-rls.sql: clinicians read every form; a clinician asks only for a patient on
-- their active panel, as themselves; a portal session reads and answers only the forms of the record it is looking at,
-- and only when it may see sensitive items, since every form is one (its request names the questionnaire, its answers
-- say how the patient has been). A proxy without sensitive items is given nothing, exactly as for a record with no
-- forms. Admins and nobody see none. Nothing at runtime updates or deletes a form.

-- The score of a form's answers against a questionnaire's definition (db/reference/36-questionnaires.sql describes its
-- shape): the sum of the answers' scores to the scored questions, the band that sum falls in, whether it is at or above
-- the follow-up value, and whether any alert question was answered with an answer that scores more than 0 (the PHQ-9's
-- question 9). With no total in the definition, no score and no band. Refuses answers that aren't a form's: an answer
-- that names no question, a question needing an answer left without one, or an answer that isn't one of its
-- question's. Its messages never repeat an answer. A pure function, run as the caller: the response guard below calls it
-- as the portal's session, so the app's role may run it, and nothing it does depends on who calls.
CREATE OR REPLACE FUNCTION ehr.questionnaire_score(definition jsonb, answers jsonb)
    RETURNS TABLE (score integer, band text, follow_up boolean, alert boolean)
    LANGUAGE plpgsql
    IMMUTABLE
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    item jsonb;
    given jsonb;
    chosen jsonb;
    total integer := 0;
    alerted boolean := false;
BEGIN
    IF jsonb_typeof(answers) IS DISTINCT FROM 'object' THEN
        RAISE EXCEPTION 'A form''s answers name each question once' USING ERRCODE = '23514';
    END IF;

    IF EXISTS (SELECT 1 FROM jsonb_object_keys(answers) AS k(code)
               WHERE NOT EXISTS (SELECT 1 FROM jsonb_array_elements(definition -> 'items') AS i(question) WHERE i.question ->> 'code' = k.code)) THEN
        RAISE EXCEPTION 'An answer names no question of the form' USING ERRCODE = '23514';
    END IF;

    FOR item IN SELECT i.question FROM jsonb_array_elements(definition -> 'items') AS i(question) LOOP
        given := answers -> (item ->> 'code');
        IF given IS NULL THEN
            IF NOT coalesce((item ->> 'optional')::boolean, false) THEN
                RAISE EXCEPTION 'Every question of the form needs an answer' USING ERRCODE = '23514';
            END IF;
            CONTINUE;
        END IF;

        chosen := NULL;
        IF jsonb_typeof(given) = 'string' THEN
            SELECT a.answer INTO chosen
            FROM jsonb_array_elements(definition -> 'answerLists' -> (item ->> 'answers')) AS a(answer)
            WHERE a.answer ->> 'code' = given #>> '{}';
        END IF;
        IF chosen IS NULL THEN
            RAISE EXCEPTION 'An answer is not one of its question''s answers' USING ERRCODE = '23514';
        END IF;

        IF coalesce((item ->> 'scored')::boolean, false) THEN
            IF jsonb_typeof(chosen -> 'score') IS DISTINCT FROM 'number' THEN
                RAISE EXCEPTION 'A scored question''s answers have scores' USING ERRCODE = '23514';
            END IF;
            total := total + (chosen ->> 'score')::integer;
            IF coalesce((item ->> 'alert')::boolean, false) AND (chosen ->> 'score')::integer > 0 THEN
                alerted := true;
            END IF;
        END IF;
    END LOOP;

    IF definition ? 'total' THEN
        score := total;
        band := (SELECT b.entry ->> 'band' FROM jsonb_array_elements(definition -> 'bands') AS b(entry)
                 WHERE total BETWEEN (b.entry ->> 'from')::integer AND (b.entry ->> 'to')::integer LIMIT 1);
        follow_up := definition ? 'followUpAt' AND total >= (definition ->> 'followUpAt')::integer;
    ELSE
        follow_up := false;
    END IF;
    alert := alerted;
    RETURN NEXT;
END
$$;
REVOKE ALL ON FUNCTION ehr.questionnaire_score(jsonb, jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ehr.questionnaire_score(jsonb, jsonb) TO ehr_app;

-- Before a request is inserted. The appointment must be one the asking clinician can read that is booked and hasn't
-- ended; the request takes its patient and its clinician from it, whatever the client sent, and a copy of the
-- questionnaire's definition as it is now, which is what the patient will answer and be scored on. Who asked and when
-- are the database's. Who may ask (a clinician, as themselves, for a patient on their active panel) is the insert
-- policy's (30-rls.sql), checked after this. Runs as the caller.
CREATE OR REPLACE FUNCTION ehr.guard_questionnaire_request() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    booked record;
BEGIN
    SELECT a.patient_id, a.clinician_id INTO booked
    FROM ehr.appointments a
    WHERE a.id = NEW.appointment_id AND a.status = 'booked' AND a.starts_at + make_interval(mins => a.minutes) > now();
    IF NOT FOUND THEN
        RAISE EXCEPTION 'A form is asked for before a booked appointment ends' USING ERRCODE = '23514';
    END IF;

    NEW.definition := (SELECT q.definition FROM ehr.questionnaires q WHERE q.code = NEW.questionnaire_code);
    IF NEW.definition IS NULL THEN
        RAISE EXCEPTION 'A form is one of the clinic''s questionnaires' USING ERRCODE = '23503';
    END IF;

    NEW.patient_id := booked.patient_id;
    NEW.clinician_id := booked.clinician_id;
    NEW.requested_by := ehr.current_user_id();
    NEW.requested_at := clock_timestamp();
    NEW.sensitive := true;
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.guard_questionnaire_request() FROM PUBLIC;
CREATE OR REPLACE TRIGGER questionnaire_requests_guard
    BEFORE INSERT ON ehr.questionnaire_requests
    FOR EACH ROW EXECUTE FUNCTION ehr.guard_questionnaire_request();

-- Before a response is inserted. The request must be one the session can read (row-level security: the record it is
-- looking at, and only with sensitive items allowed), from 7 days before its appointment's start, on the database's
-- clock, while the appointment is booked and hasn't reached its scheduled end, or is checked in and either hasn't
-- reached it or its visit is still in progress, up to 12 hours after the scheduled end (from the step's review: a late
-- visit doesn't close the form under the patient, and a checked-in appointment's form still has an end). The 12 hours
-- are the window the vital-signs rule gives a visit (75-results.sql); the coordinator's alternative, the end of the
-- clinic's day, can't be held here, since the clinic's time zone is the app's setting (Ehr:Clinic:TimeZone), which the
-- database is never given, and a session's TimeZone is the client's to set. A cancellation or a no-show closes it, and
-- so does the visit's end (no page ends a visit yet: nothing updates one, 10-grants.sql). A request that doesn't exist, another patient's, one a proxy
-- without sensitive items may not see, one not open yet and one closed are all refused alike, with one error, before
-- anything else is looked at, so a refusal says nothing of which it was. The answers are scored against the request's
-- copy of the questionnaire; the patient, the questionnaire, the clinician, the score, its band, follow-up and alert,
-- who answered, under which proxy grant, and when are all the database's, whatever the client sent. The insert policy
-- (30-rls.sql) then holds the row to the session that sent it: its record, its account and its grant, each asked once
-- for the statement, so a statement that changed who it is between rows is refused. One response for a request: a
-- unique index. Runs as the caller.
CREATE OR REPLACE FUNCTION ehr.guard_questionnaire_response() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    asked record;
    scored record;
BEGIN
    SELECT r.patient_id, r.questionnaire_code, r.clinician_id, r.definition INTO asked
    FROM ehr.questionnaire_requests r
    JOIN ehr.appointments a ON a.id = r.appointment_id
    WHERE r.id = NEW.request_id AND now() >= a.starts_at - interval '7 days'
      AND (a.status = 'booked' AND now() < a.starts_at + make_interval(mins => a.minutes)
           OR a.status = 'fulfilled' AND (now() < a.starts_at + make_interval(mins => a.minutes)
               OR now() < a.starts_at + make_interval(mins => a.minutes) + interval '12 hours'
                  AND EXISTS (SELECT 1 FROM ehr.encounters e WHERE e.id = a.encounter_id AND e.patient_id = a.patient_id AND e.status = 'in-progress')));
    IF NOT FOUND THEN
        RAISE EXCEPTION 'The form can''t be answered' USING ERRCODE = '42501';
    END IF;

    SELECT s.score, s.band, s.follow_up, s.alert INTO scored FROM ehr.questionnaire_score(asked.definition, NEW.answers) s;
    NEW.patient_id := asked.patient_id;
    NEW.questionnaire_code := asked.questionnaire_code;
    NEW.clinician_id := asked.clinician_id;
    NEW.score := scored.score;
    NEW.band := scored.band;
    NEW.follow_up := scored.follow_up;
    NEW.alert := scored.alert;
    NEW.answered_by := ehr.current_user_id();
    NEW.proxy_grant_id := ehr.acting_grant_id();
    NEW.answered_at := clock_timestamp();
    NEW.sensitive := true;
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.guard_questionnaire_response() FROM PUBLIC;
CREATE OR REPLACE TRIGGER questionnaire_responses_guard
    BEFORE INSERT ON ehr.questionnaire_responses
    FOR EACH ROW EXECUTE FUNCTION ehr.guard_questionnaire_response();

-- A form is a record: no row of a request or its answers is ever updated or deleted, whoever tries, the tables' owner
-- included. The runtime roles have no UPDATE or DELETE on either (10-grants.sql); this holds for everyone else. A row
-- trigger can't stop TRUNCATE, which is the tables' owner's alone, as for every table, and which nothing here runs.
CREATE OR REPLACE FUNCTION ehr.reject_questionnaire_change() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    RAISE EXCEPTION 'A pre-visit form is never changed' USING ERRCODE = '42501';
END
$$;
REVOKE ALL ON FUNCTION ehr.reject_questionnaire_change() FROM PUBLIC;
CREATE OR REPLACE TRIGGER questionnaire_requests_immutable
    BEFORE UPDATE OR DELETE ON ehr.questionnaire_requests
    FOR EACH ROW EXECUTE FUNCTION ehr.reject_questionnaire_change();
CREATE OR REPLACE TRIGGER questionnaire_responses_immutable
    BEFORE UPDATE OR DELETE ON ehr.questionnaire_responses
    FOR EACH ROW EXECUTE FUNCTION ehr.reject_questionnaire_change();

-- An answer that needs review (an alert question, the PHQ-9's question 9, answered other than "Not at all") is told to
-- the patient's care team, written once here and run in two places: when the answer is stored (below), and, from the
-- step's review, when an account holding an item for it is disabled (further below). Unless a clinician of the team who
-- can still sign in already holds an open item for the answer, one inbox item each for the team's clinicians whose
-- accounts aren't disabled: the clinician the appointment is with and every clinician with the patient on their active
-- panel (an assignment that has ended doesn't count); or, when none of them can be told, every clinician whose account
-- isn't disabled, so an answer isn't left with nobody told while anyone can be. Never a second open item for someone
-- who holds one. A clinician locked for a few minutes after wrong passwords still counts and is still told; one an admin
-- disabled (locked out for good, 80-appointments.sql's test) neither. With no clinician account enabled at all, nobody:
-- the answer is stored with its alert, and the forms page shows it (docs/UC22.md, Limits). The item is marked sensitive
-- (and a check on inbox_items holds every form-alert item to that), so the write audit marks its events and the admin
-- audit view shows none of their values; its summary names no questionnaire, answer or score, and it links to the
-- answers. Each recipient marks their own done. Nobody is emailed. Runs as its caller, which is only ever the tables'
-- owner, from the two owner-run triggers below (nobody else may run it): through the owner's policies on inbox_items
-- and patient_assignments (30-rls.sql), and staff_users, which has none; it reads no other table. Its search_path is
-- pinned. Returns how many it told.
CREATE OR REPLACE FUNCTION ehr.tell_care_team_of_answer(response uuid, patient uuid, clinician uuid) RETURNS integer
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    told integer;
BEGIN
    IF EXISTS (
        SELECT 1 FROM ehr.inbox_items i
        JOIN ehr.staff_users s ON s.id = i.recipient_id
        WHERE i.source_table = 'questionnaire_responses' AND i.source_id = response AND i.done_at IS NULL
          AND (s.lockout_end IS NULL OR s.lockout_end < now() + interval '10 years')
          AND (s.id = clinician
               OR EXISTS (SELECT 1 FROM ehr.patient_assignments a WHERE a.clinician_id = s.id AND a.patient_id = patient AND a.ended_at IS NULL))) THEN
        RETURN 0;
    END IF;

    INSERT INTO ehr.inbox_items (id, recipient_id, kind, patient_id, source_table, source_id, summary, created_at, sensitive)
    SELECT uuidv7(), s.id, 'form-alert', patient, 'questionnaire_responses', response,
           'A pre-visit form has an answer that needs review', clock_timestamp(), true
    FROM ehr.staff_users s
    WHERE s.roles @> ARRAY['Clinician'] AND (s.lockout_end IS NULL OR s.lockout_end < now() + interval '10 years')
      AND (s.id = clinician
           OR s.id IN (SELECT a.clinician_id FROM ehr.patient_assignments a WHERE a.patient_id = patient AND a.ended_at IS NULL))
      AND NOT EXISTS (SELECT 1 FROM ehr.inbox_items i
                      WHERE i.recipient_id = s.id AND i.source_table = 'questionnaire_responses' AND i.source_id = response AND i.done_at IS NULL);
    GET DIAGNOSTICS told = ROW_COUNT;
    IF told = 0 THEN
        INSERT INTO ehr.inbox_items (id, recipient_id, kind, patient_id, source_table, source_id, summary, created_at, sensitive)
        SELECT uuidv7(), s.id, 'form-alert', patient, 'questionnaire_responses', response,
               'A pre-visit form has an answer that needs review', clock_timestamp(), true
        FROM ehr.staff_users s
        WHERE s.roles @> ARRAY['Clinician'] AND (s.lockout_end IS NULL OR s.lockout_end < now() + interval '10 years')
          AND NOT EXISTS (SELECT 1 FROM ehr.inbox_items i
                          WHERE i.recipient_id = s.id AND i.source_table = 'questionnaire_responses' AND i.source_id = response AND i.done_at IS NULL);
        GET DIAGNOSTICS told = ROW_COUNT;
    END IF;
    RETURN told;
END
$$;
REVOKE ALL ON FUNCTION ehr.tell_care_team_of_answer(uuid, uuid, uuid) FROM PUBLIC;

-- When an answer is stored: told at once, in the same statement. First a shared hold on the alerts' lock (an advisory
-- lock on 'form-alert holders'), which an account being disabled takes alone (below): so an answer told to someone
-- at the moment their account is disabled is either seen by the disable, which tells it again, or waits for it and
-- leaves that account out. Answers don't wait for each other. A disable holds the lock only while its transaction is
-- open, which from the Staff page is one request; but an admin's open SQL session could hold it for as long as it
-- stays open, so the wait is bounded here, by the patient's side (from the step's third check): at most 5 seconds,
-- with lock_timeout set for that one step and put back after. If the lock isn't got by then, the answer is stored
-- all the same, never refused or lost for an admin's slow transaction, and, without the lock to keep the race closed,
-- it is told more widely (from the step's last check): by the rule, and also to every clinician whose account isn't
-- disabled who doesn't hold an open item for it, so it can't rest with an account being disabled at that moment alone.
-- What remains is a transaction that is disabling every clinician account the answer sees, all at once: then, as when
-- no clinician account is enabled, nobody who can sign in holds it (a clinician enabled or joining later is given it).
-- A rare path, a disable held open past 5 seconds, so the extra items are a fair price. Runs as its owner, to write the
-- inbox, which no runtime role may (10-grants.sql): it reads the staff accounts and the inbox itself only on that path.
-- Its search_path is pinned, and nobody may call it: only the trigger runs it.
CREATE OR REPLACE FUNCTION ehr.deliver_questionnaire_alert() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    previous text := current_setting('lock_timeout');
    locked boolean := true;
BEGIN
    PERFORM set_config('lock_timeout', '5s', true);
    BEGIN
        PERFORM pg_advisory_xact_lock_shared(hashtextextended('form-alert holders', 0));
    EXCEPTION WHEN lock_not_available THEN
        locked := false;
    END;
    PERFORM set_config('lock_timeout', previous, true);
    PERFORM ehr.tell_care_team_of_answer(NEW.id, NEW.patient_id, NEW.clinician_id);
    IF NOT locked THEN
        INSERT INTO ehr.inbox_items (id, recipient_id, kind, patient_id, source_table, source_id, summary, created_at, sensitive)
        SELECT uuidv7(), s.id, 'form-alert', NEW.patient_id, 'questionnaire_responses', NEW.id,
               'A pre-visit form has an answer that needs review', clock_timestamp(), true
        FROM ehr.staff_users s
        WHERE s.roles @> ARRAY['Clinician'] AND (s.lockout_end IS NULL OR s.lockout_end < now() + interval '10 years')
          AND NOT EXISTS (SELECT 1 FROM ehr.inbox_items i
                          WHERE i.recipient_id = s.id AND i.source_table = 'questionnaire_responses' AND i.source_id = NEW.id AND i.done_at IS NULL);
    END IF;
    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.deliver_questionnaire_alert() FROM PUBLIC;
CREATE OR REPLACE TRIGGER questionnaire_responses_deliver_alert
    AFTER INSERT ON ehr.questionnaire_responses
    FOR EACH ROW WHEN (NEW.alert)
    EXECUTE FUNCTION ehr.deliver_questionnaire_alert();

-- When an admin disables an account (the Staff page locks it out for good: StaffAccounts.DisableAsync, as ehr_app), each
-- answer it holds an open item for is told again by the rule above, so one held only by someone who can no longer sign
-- in reaches the team, or everyone (from the step's review). The disabled account keeps its items. Whatever disables
-- an account is seen here, since the database watches the column, not the page; a lockout of a few minutes after wrong
-- passwords isn't a disable, and enabling an account again does nothing. Under the alerts' lock, alone (above): two
-- accounts disabled at once are told in turn, the second seeing the first, and an answer being stored, or a clinician
-- joining a panel, waits or is waited for. While it holds the lock it waits for nothing (it reads, and adds inbox
-- items), so it can be in no circle of waits. A transaction disabling several accounts holds it once. Runs as its owner, to read the answers (the
-- owner's policy, 30-rls.sql) and the inbox and to write the inbox; its search_path is pinned, and nobody may call it:
-- only the trigger runs it.
CREATE OR REPLACE FUNCTION ehr.follow_disabled_form_alert_holder() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    held record;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtextextended('form-alert holders', 0));
    FOR held IN
        SELECT DISTINCT r.id, r.patient_id, r.clinician_id
        FROM ehr.inbox_items i
        JOIN ehr.questionnaire_responses r ON r.id = i.source_id
        WHERE i.recipient_id = NEW.id AND i.kind = 'form-alert' AND i.source_table = 'questionnaire_responses' AND i.done_at IS NULL
        ORDER BY r.id
    LOOP
        PERFORM ehr.tell_care_team_of_answer(held.id, held.patient_id, held.clinician_id);
    END LOOP;
    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.follow_disabled_form_alert_holder() FROM PUBLIC;
CREATE OR REPLACE TRIGGER staff_users_disabled_form_alerts
    AFTER UPDATE OF lockout_end ON ehr.staff_users
    FOR EACH ROW
    WHEN (NEW.lockout_end >= now() + interval '10 years' AND (OLD.lockout_end IS NULL OR OLD.lockout_end < now() + interval '10 years'))
    EXECUTE FUNCTION ehr.follow_disabled_form_alert_holder();

-- An answer that needs review follows the care team, so it never sits where none of them is looking (from the step's
-- review, in the manner of ehr.follow_refill_care_team, 77-refill-requests.sql). When a clinician joins a patient's
-- panel, they get an item for each of that patient's answers to review that has no item still open in the inbox of a
-- clinician now on the panel or of the appointment's clinician who can still sign in (a disabled account's open item
-- tells nobody); one they hold open already counts. Their account must not be disabled. Runs as its owner (SECURITY
-- DEFINER), which alone may add inbox items: it reads the answers (the owner's policy, 30-rls.sql), the panels, the
-- staff accounts and the inbox, and writes the inbox; nothing else. Its search_path is pinned, and nobody but its owner
-- may call it directly. Before it reads, it takes the alerts' lock shared (above; from the step's third check), so a
-- clinician whose own account is disabled at the moment they join can't be left holding an answer's only open item:
-- the disable waits for the join and tells it again, or the join waits for the disable and gives them nothing. An
-- answer arriving while a clinician joins needs no lock: it is told by its own trigger, above, to the team it sees,
-- or, seeing nobody, to every clinician, the one joining among them; and nothing closes another's form-alert item, so
-- none is left open that its holder can't act on. Lock order with step 13d (77-refill-requests.sql): this trigger
-- fires before 13d's on the same insert (AFTER triggers fire in name order), so a join takes this lock, shared, and
-- then the patient's care-team lock. Nothing takes them the other way round to wait: a disable, the only taker of this
-- lock alone, takes no care-team lock and waits for nothing while it holds it; answers and joins hold it shared, and
-- shared holds don't wait for each other. So the two locks close no circle.
CREATE OR REPLACE FUNCTION ehr.follow_questionnaire_alert_care_team() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    PERFORM pg_advisory_xact_lock_shared(hashtextextended('form-alert holders', 0));
    INSERT INTO ehr.inbox_items (id, recipient_id, kind, patient_id, source_table, source_id, summary, created_at, sensitive)
    SELECT uuidv7(), s.id, 'form-alert', r.patient_id, 'questionnaire_responses', r.id,
           'A pre-visit form has an answer that needs review', clock_timestamp(), true
    FROM ehr.questionnaire_responses r
    JOIN ehr.staff_users s ON s.id = NEW.clinician_id
    WHERE r.patient_id = NEW.patient_id AND r.alert
      AND s.roles @> ARRAY['Clinician'] AND (s.lockout_end IS NULL OR s.lockout_end < now() + interval '10 years')
      AND NOT EXISTS (
          SELECT 1 FROM ehr.inbox_items i
          JOIN ehr.staff_users holder ON holder.id = i.recipient_id
          WHERE i.source_table = 'questionnaire_responses' AND i.source_id = r.id AND i.done_at IS NULL
            AND (holder.lockout_end IS NULL OR holder.lockout_end < now() + interval '10 years')
            AND (i.recipient_id = r.clinician_id
                 OR EXISTS (SELECT 1 FROM ehr.patient_assignments a
                            WHERE a.clinician_id = i.recipient_id AND a.patient_id = r.patient_id AND a.ended_at IS NULL)));
    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.follow_questionnaire_alert_care_team() FROM PUBLIC;
CREATE OR REPLACE TRIGGER patient_assignments_join_form_alerts
    AFTER INSERT ON ehr.patient_assignments
    FOR EACH ROW WHEN (NEW.ended_at IS NULL)
    EXECUTE FUNCTION ehr.follow_questionnaire_alert_care_team();
