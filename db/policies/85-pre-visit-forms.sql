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
-- looking at, and only with sensitive items allowed) whose appointment is booked or checked in, from 7 days before its
-- start until its scheduled end, on the database's clock. A request that doesn't exist, another patient's, one a proxy
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
    WHERE r.id = NEW.request_id AND a.status IN ('booked', 'fulfilled')
      AND now() >= a.starts_at - interval '7 days' AND now() < a.starts_at + make_interval(mins => a.minutes);
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

-- A form is a record: a request and its answers never change and are never deleted, whoever tries, the tables' owner
-- included. The runtime roles have no UPDATE or DELETE on either (10-grants.sql); this holds for everyone else.
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

-- An answer that needs review (the PHQ-9's question 9 answered other than "Not at all"): one inbox item, in the same
-- statement, for the clinician the appointment is with and for every clinician with the patient on their active panel,
-- each once, whose account isn't disabled (one locked for a few minutes after wrong passwords still gets it). The item
-- is marked sensitive, so the write audit marks its events and the admin audit view shows none of their values; its
-- summary names no questionnaire, answer or score, and it links to the answers. Each recipient marks their own done.
-- Nobody is emailed. Runs as its owner, to write the inbox, which no runtime role may (10-grants.sql): through the owner's
-- policies on inbox_items and patient_assignments (30-rls.sql), and staff_users, which has none; it reads no other
-- table. Its search_path is pinned, and nobody may call it: only the trigger runs it.
CREATE OR REPLACE FUNCTION ehr.deliver_questionnaire_alert() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    INSERT INTO ehr.inbox_items (id, recipient_id, kind, patient_id, source_table, source_id, summary, created_at, sensitive)
    SELECT uuidv7(), s.id, 'form-alert', NEW.patient_id, 'questionnaire_responses', NEW.id,
           'A pre-visit form has an answer that needs review', clock_timestamp(), true
    FROM ehr.staff_users s
    WHERE s.roles @> ARRAY['Clinician'] AND (s.lockout_end IS NULL OR s.lockout_end < now() + interval '10 years')
      AND (s.id = NEW.clinician_id
           OR s.id IN (SELECT a.clinician_id FROM ehr.patient_assignments a WHERE a.patient_id = NEW.patient_id AND a.ended_at IS NULL));
    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.deliver_questionnaire_alert() FROM PUBLIC;
CREATE OR REPLACE TRIGGER questionnaire_responses_deliver_alert
    AFTER INSERT ON ehr.questionnaire_responses
    FOR EACH ROW WHEN (NEW.alert)
    EXECUTE FUNCTION ehr.deliver_questionnaire_alert();
