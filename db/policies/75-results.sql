-- Results of an order, and their acknowledgment (the expansion plan's step 7): what the database itself holds them
-- to, whatever the app does. Who may file and acknowledge is row-level security's (30-rls.sql): a clinician with the
-- patient on their active panel, or, for filing, a lab through its door (step 12, 76-lab.sql), whose rows are held
-- to the same rules here. Plain SQL, safe to run again.

-- Before a result or a vital sign is inserted. Runs as the caller.
--   1. It answers a lab order of the same patient that is still open to results: active, or completed and not yet
--      acknowledged, so results that arrive in parts can be added until someone has signed them off.
--   2. It is for one of the tests the order's catalog item reports (ehr.order_result_codes), and its name, unit and
--      reference range are the database's own for that test (ehr.observation_codes), whatever the client sent: the
--      range is what marks a result abnormal, so it is never the client's to choose.
--   3. It is a final, numeric result, collected no later than now (it may be before the order was signed: an order
--      is sometimes entered after the sample was drawn), and released now: the portal shows it at once.
CREATE OR REPLACE FUNCTION ehr.file_result() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    ordered ehr.orders%ROWTYPE;
    test ehr.observation_codes%ROWTYPE;
    lowest numeric;
    highest numeric;
    moment_known boolean;
    moment_visit uuid;
BEGIN
    -- As for signing an order (60-orders.sql): under a snapshot taken earlier, the checks below could miss an
    -- acknowledgment that has since been committed.
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'A result is filed in a READ COMMITTED transaction' USING ERRCODE = '0A000';
    END IF;

    -- A vital sign (step 9) answers no order. It is one of the vital-sign codes, taken no later than now, at the visit
    -- its moment was decided to belong to (step 9c); its name, unit and reference range are the database's own, as for
    -- a result; it is final and released at once. A body mass index is never the client's number: it is worked out here
    -- from the weight and height recorded for the same moment, and refused without them. A blood pressure value is one
    -- half of a reading (step 9c).
    IF NEW.order_id IS NULL THEN
        IF NEW.category IS DISTINCT FROM 'vital-signs' THEN
            RAISE EXCEPTION 'A result is filed against the order that asked for it' USING ERRCODE = '23514';
        END IF;

        SELECT c.* INTO test FROM ehr.observation_codes c WHERE c.code = NEW.code AND c.kind = 'vital';
        IF NOT FOUND THEN
            RAISE EXCEPTION 'That is not a vital sign this system records' USING ERRCODE = '23514';
        END IF;

        IF NEW.effective_at IS NULL OR NEW.effective_at > now() + interval '5 minutes'
           OR NEW.effective_at < now() - interval '130 years' THEN
            RAISE EXCEPTION 'A vital sign is taken in a lifetime, and not in the future' USING ERRCODE = '23514';
        END IF;

        -- One moment of one patient at a time (step 9c): a value for a patient and time waits here while another
        -- transaction records one for the same, and then finds it, with its visit and its reading. Keyed on the
        -- instant, not its text, so the session's time zone can't change the key. And no visit of the patient starts
        -- meanwhile: a check-in waits for this transaction, or this for the check-in, so the value is either there
        -- for the check-in to tie to its visit or finds the visit itself (ehr.attach_vital_signs, below).
        PERFORM pg_advisory_xact_lock(hashtextextended('ehr.observations:' || NEW.patient_id::text || ':' || extract(epoch FROM NEW.effective_at)::text, 0));
        PERFORM pg_advisory_xact_lock_shared(hashtextextended('ehr.encounters:' || NEW.patient_id::text, 0));

        -- The visit it was taken at is decided from the time it was taken alone (steps 9c and, after Tahseen's answers,
        -- 9c's follow-up), not the moment its row is inserted: the patient's visit that was held and began no later
        -- than fifteen minutes after it was taken, within the twelve hours before, the latest of them, if there is
        -- one. So a sign taken in the waiting room up to fifteen minutes before the check-in belongs to the visit. The
        -- first value recorded for the patient at that time decides it, and every later value of the moment,
        -- standing or entered in error, takes the same visit whatever it names, so a check-in between two values of
        -- one moment can't split them; a check-in after the moment was recorded ties the whole moment to its visit
        -- when the rule gives that visit (ehr.attach_vital_signs, below), so typing a set before or after the
        -- check-in ends the same. A visit the client names for the first value must be the rule's. The chart and the
        -- offline copy show a visit's vital signs with the visit.
        SELECT o.encounter_id, true INTO moment_visit, moment_known
        FROM ehr.observations o
        WHERE o.patient_id = NEW.patient_id AND o.category = 'vital-signs' AND o.effective_at = NEW.effective_at
        ORDER BY o.issued_at, o.id
        LIMIT 1;
        IF NOT coalesce(moment_known, false) THEN
            SELECT e.id INTO moment_visit
            FROM ehr.encounters e
            WHERE e.patient_id = NEW.patient_id AND e.status NOT IN ('planned', 'cancelled', 'entered-in-error')
              AND e.period_start <= NEW.effective_at + interval '15 minutes'
              AND e.period_start >= NEW.effective_at - interval '12 hours'
            ORDER BY e.period_start DESC, e.id
            LIMIT 1;
            IF NEW.encounter_id IS NOT NULL AND NEW.encounter_id IS DISTINCT FROM moment_visit THEN
                RAISE EXCEPTION 'A vital sign belongs to the visit of the same patient that was held when it was taken, or began within fifteen minutes after'
                    USING ERRCODE = '23514';
            END IF;
        END IF;
        NEW.encounter_id := moment_visit;

        IF NEW.code = '39156-5' THEN
            SELECT round(w.value / ((h.value / 100) ^ 2), 1) INTO NEW.value
            FROM ehr.observations w, ehr.observations h
            WHERE w.patient_id = NEW.patient_id AND w.code = '29463-7' AND w.effective_at = NEW.effective_at AND w.status = 'final'
              AND h.patient_id = NEW.patient_id AND h.code = '8302-2' AND h.effective_at = NEW.effective_at AND h.value > 0
              AND h.status = 'final' -- never from a weight or height marked entered in error (step 9b)
            ORDER BY w.issued_at DESC, h.issued_at DESC
            LIMIT 1;
            IF NEW.value IS NULL THEN
                RAISE EXCEPTION 'A body mass index needs the weight and height taken with it' USING ERRCODE = '23514';
            END IF;
        END IF;

        -- A value a person can have: the widest bounds for each sign, the same ones the form states. The weight and
        -- height bounds keep the body mass index's arithmetic inside what the column holds; its own bound is checked
        -- after it is worked out.
        SELECT b.lowest, b.highest INTO lowest, highest
        FROM (VALUES ('8480-6', 1, 350), ('8462-4', 1, 250), ('8867-4', 1, 350), ('8310-5', 20, 45), ('9279-1', 1, 100),
                     ('59408-5', 1, 100), ('29463-7', 0.2, 700), ('8302-2', 20, 280), ('39156-5', 1, 500)) AS b(code, lowest, highest)
        WHERE b.code = NEW.code;
        IF NOT FOUND OR NEW.value IS NULL OR NEW.value < lowest OR NEW.value > highest THEN
            RAISE EXCEPTION 'That value is outside what this vital sign can be' USING ERRCODE = '23514';
        END IF;

        NEW.display := left(test.display, 200);
        NEW.unit := left(test.unit, 20);
        NEW.reference_low := test.reference_low;
        NEW.reference_high := test.reference_high;
        NEW.reference_kind := 'normal';
        -- Heart rate, respiratory rate and blood pressure have a range for each age, and a child's blood pressure for
        -- each sex too (steps 9b and 9d): the patient's age in whole months on the day the sign was taken picks it,
        -- and their sex. A patient whose sex is other or unknown is held to both sexes' rows: the higher low and the
        -- lower high, so flagged sooner. A child's blood pressure range is a screening value (kind 'screening': a value
        -- at or above it needs follow-up). A sign with ranges by age but none for this age (blood pressure under 1
        -- year) has no range, rather than the adults'; a sign with none by age keeps the one above.
        IF EXISTS (SELECT 1 FROM ehr.vital_reference_ranges r WHERE r.code = NEW.code) THEN
            SELECT max(r.low), min(r.high), coalesce(max(r.kind), 'normal') INTO NEW.reference_low, NEW.reference_high, NEW.reference_kind
            FROM ehr.vital_reference_ranges r
            JOIN ehr.patients p ON p.id = NEW.patient_id
            CROSS JOIN LATERAL (
                -- Between two timestamps without a zone, so the session's time zone can't move a month boundary.
                SELECT (extract(year FROM age((NEW.effective_at AT TIME ZONE 'UTC')::date::timestamp, p.birth_date::timestamp)) * 12
                        + extract(month FROM age((NEW.effective_at AT TIME ZONE 'UTC')::date::timestamp, p.birth_date::timestamp)))::int AS months) a
            WHERE r.code = NEW.code AND greatest(a.months, 0) >= r.from_months AND (r.to_months IS NULL OR greatest(a.months, 0) < r.to_months)
              AND (r.sex = 'any' OR r.sex = p.gender OR p.gender NOT IN ('male', 'female'));
        END IF;
        -- Blood pressure is one reading (step 9c): a systolic and its diastolic share a reading ID, the database's,
        -- whatever the client sent. A value joins the standing reading of its moment that lacks it, which is its
        -- partner entered in the same transaction; otherwise it starts a new one. The reading is checked whole when
        -- the transaction commits (ehr.check_vital_reading, below).
        IF NEW.code IN ('8480-6', '8462-4') THEN
            SELECT o.reading_id INTO NEW.reading_id
            FROM ehr.observations o
            WHERE o.patient_id = NEW.patient_id AND o.category = 'vital-signs' AND o.effective_at = NEW.effective_at
              AND o.code IN ('8480-6', '8462-4') AND o.code <> NEW.code AND o.status <> 'entered-in-error'
              AND NOT EXISTS (SELECT 1 FROM ehr.observations s WHERE s.reading_id = o.reading_id AND s.code = NEW.code)
            LIMIT 1;
            NEW.reading_id := coalesce(NEW.reading_id, uuidv7());
        ELSE
            NEW.reading_id := NULL;
        END IF;
        NEW.status := 'final';
        NEW.issued_at := clock_timestamp();
        NEW.note := NULL;
        NEW.entered_in_error_reason := NULL;
        NEW.entered_in_error_by := NULL;
        NEW.entered_in_error_at := NULL;
        RETURN NEW;
    END IF;

    IF NEW.category IS DISTINCT FROM 'laboratory' OR NEW.encounter_id IS NOT NULL THEN
        RAISE EXCEPTION 'A result of an order is a laboratory result' USING ERRCODE = '23514';
    END IF;

    -- The order's row is locked, so a result and an acknowledgment of the same order never pass each other.
    SELECT * INTO ordered FROM ehr.orders o WHERE o.id = NEW.order_id AND o.patient_id = NEW.patient_id FOR NO KEY UPDATE;
    IF NOT FOUND OR ordered.type <> 'lab' OR ordered.status NOT IN ('active', 'completed') THEN
        RAISE EXCEPTION 'Results are filed against an active lab order of the same patient' USING ERRCODE = '23514';
    END IF;

    IF EXISTS (SELECT 1 FROM ehr.result_acknowledgments a WHERE a.order_id = NEW.order_id) THEN
        RAISE EXCEPTION 'The results of this order have been acknowledged; nothing is added to them' USING ERRCODE = '23514';
    END IF;

    SELECT c.* INTO test
    FROM ehr.order_result_codes r JOIN ehr.observation_codes c ON c.code = r.observation_code
    WHERE r.catalog_code = ordered.catalog_code AND r.observation_code = NEW.code;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'That test is not one this order reports' USING ERRCODE = '23514';
    END IF;

    IF NEW.value IS NULL THEN
        RAISE EXCEPTION 'A result has a value' USING ERRCODE = '23514';
    END IF;

    IF NEW.effective_at IS NULL OR NEW.effective_at > now() + interval '5 minutes' THEN
        RAISE EXCEPTION 'A sample is not collected in the future' USING ERRCODE = '23514';
    END IF;

    NEW.display := left(test.display, 200);
    NEW.unit := left(test.unit, 20);
    NEW.reference_low := test.reference_low;
    NEW.reference_high := test.reference_high;
    NEW.reference_kind := 'normal';
    NEW.reading_id := NULL;
    NEW.status := 'final';
    NEW.issued_at := clock_timestamp();
    NEW.entered_in_error_reason := NULL;
    NEW.entered_in_error_by := NULL;
    NEW.entered_in_error_at := NULL;
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.file_result() FROM PUBLIC;
CREATE OR REPLACE TRIGGER observations_file
    BEFORE INSERT ON ehr.observations
    -- For every insert the apps make, by the role they connect as: never by who the statement says is signed in,
    -- which a statement can change between its own rows. That is the role the session logged in as (session_user)
    -- as well as the one a statement runs as (current_user), so an insert an app session makes through a function
    -- that runs as its owner, as the lab's door does (76-lab.sql), passes through here too. Rows loaded by the
    -- schema's owner logged in as itself (the seed, the migrations) or by a superuser (a test's setup) don't.
    FOR EACH ROW WHEN (current_user IN ('ehr_app', 'ehr_read') OR session_user IN ('ehr_app', 'ehr_read'))
    EXECUTE FUNCTION ehr.file_result();

-- After a result is inserted: its order, if still active, becomes completed. As the caller, who may update the orders
-- of a patient on their panel, or the lab's door, as the tables' owner, whose policy lets it complete a lab order
-- (orders_owner_update, 30-rls.sql); the orders' own trigger allows exactly this change (60-orders.sql).
CREATE OR REPLACE FUNCTION ehr.complete_resulted_order() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    UPDATE ehr.orders SET status = 'completed' WHERE id = NEW.order_id AND patient_id = NEW.patient_id AND status = 'active';
    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.complete_resulted_order() FROM PUBLIC;
-- Marking a vital sign "entered in error" (the expansion plan's step 9b; FHIR's entered-in-error status). The one
-- change an observation takes from the apps, which hold UPDATE on these two columns only (10-grants.sql), under the
-- panel rule (30-rls.sql). Before the update, as the caller: only a vital sign, only one that is final, only to
-- entered-in-error, with a reason; who and when are the database's. Nothing unmarks one: the correct value is a new
-- reading. A result of an order is not corrected this way.
CREATE OR REPLACE FUNCTION ehr.mark_vital_sign_in_error() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF OLD.category IS DISTINCT FROM 'vital-signs' OR OLD.order_id IS NOT NULL THEN
        RAISE EXCEPTION 'Only a vital sign is marked entered in error' USING ERRCODE = '23514';
    END IF;
    IF OLD.status <> 'final' OR NEW.status IS DISTINCT FROM 'entered-in-error' THEN
        RAISE EXCEPTION 'A recorded vital sign changes only by being marked entered in error, once' USING ERRCODE = '23514';
    END IF;
    NEW.entered_in_error_reason := btrim(NEW.entered_in_error_reason);
    IF NEW.entered_in_error_reason IS NULL OR NEW.entered_in_error_reason !~ '[^[:space:]]' OR char_length(NEW.entered_in_error_reason) > 500 THEN
        RAISE EXCEPTION 'Say why the vital sign was entered in error, in up to 500 characters' USING ERRCODE = '23514';
    END IF;
    NEW.entered_in_error_by := ehr.current_user_id();
    NEW.entered_in_error_at := clock_timestamp();
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.mark_vital_sign_in_error() FROM PUBLIC;
CREATE OR REPLACE TRIGGER observations_mark_in_error
    BEFORE UPDATE ON ehr.observations
    -- By the role the apps connect as, as for filing (observations_file above).
    FOR EACH ROW WHEN (current_user IN ('ehr_app', 'ehr_read'))
    EXECUTE FUNCTION ehr.mark_vital_sign_in_error();

-- A body mass index is worked out from a weight and a height: when either is marked entered in error, so is the body
-- mass index of the same moment, with the same reason, by the same person. As the caller, through the rule above.
CREATE OR REPLACE FUNCTION ehr.mark_body_mass_index_in_error() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    UPDATE ehr.observations o
    SET status = 'entered-in-error', entered_in_error_reason = NEW.entered_in_error_reason
    WHERE o.patient_id = NEW.patient_id AND o.category = 'vital-signs' AND o.code = '39156-5'
      AND o.effective_at = NEW.effective_at AND o.status = 'final';
    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.mark_body_mass_index_in_error() FROM PUBLIC;
CREATE OR REPLACE TRIGGER observations_mark_body_mass_index
    AFTER UPDATE ON ehr.observations
    FOR EACH ROW WHEN (NEW.status = 'entered-in-error' AND OLD.status = 'final' AND NEW.code IN ('29463-7', '8302-2'))
    EXECUTE FUNCTION ehr.mark_body_mass_index_in_error();

-- A blood pressure is one reading (step 9c): marking either of its values entered in error marks the other too, in the
-- same statement, with the same reason. As the caller, through the rule above and the panel policy (30-rls.sql), so
-- each value is its own audited change. Only as the person who marked the first: a statement that has changed who is
-- signed in since then is refused, so it can't name someone else for the other value (step 9b's rule for one row).
CREATE OR REPLACE FUNCTION ehr.mark_reading_in_error() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF current_user IN ('ehr_app', 'ehr_read') AND NEW.entered_in_error_by IS DISTINCT FROM ehr.current_user_id() THEN
        RAISE EXCEPTION 'A reading is marked entered in error by one person' USING ERRCODE = '42501';
    END IF;
    UPDATE ehr.observations o
    SET status = 'entered-in-error', entered_in_error_reason = NEW.entered_in_error_reason
    WHERE o.reading_id = NEW.reading_id AND o.id <> NEW.id AND o.status <> 'entered-in-error';
    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.mark_reading_in_error() FROM PUBLIC;
CREATE OR REPLACE TRIGGER observations_mark_reading
    AFTER UPDATE ON ehr.observations
    FOR EACH ROW WHEN (NEW.status = 'entered-in-error' AND OLD.status <> 'entered-in-error' AND NEW.reading_id IS NOT NULL)
    EXECUTE FUNCTION ehr.mark_reading_in_error();

-- A blood pressure reading (step 9c) is a systolic and a diastolic of one patient, taken at one time at one visit, that
-- stand together or are entered in error together: when a transaction commits, each reading it touched has both values
-- standing or none. So a lone value can't be left behind, and a correction is a new reading (a value can't join one
-- whose other value is in error). Constraint triggers, deferred to the commit so the two values can be inserted one
-- after the other; for every role, the tables' owner and superusers too. The function runs as its owner so it sees every
-- value of the reading whoever the session says is signed in (observations_owner_select, 30-rls.sql): a check that saw
-- nothing would pass. Two systolics or two diastolics in one reading are refused at once by an index
-- (ux_observations_reading), and a reading's values are only blood pressures (ck_observations_reading).
CREATE OR REPLACE FUNCTION ehr.check_vital_reading() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    readings uuid[] := ARRAY[]::uuid[];
    reading uuid;
BEGIN
    IF TG_OP <> 'DELETE' THEN
        readings := readings || NEW.reading_id;
    END IF;
    IF TG_OP <> 'INSERT' THEN
        readings := readings || OLD.reading_id;
    END IF;
    FOREACH reading IN ARRAY readings LOOP
        CONTINUE WHEN reading IS NULL;
        IF EXISTS (
            SELECT 1 FROM ehr.observations o WHERE o.reading_id = reading
            HAVING count(DISTINCT o.patient_id) > 1 OR count(DISTINCT o.effective_at) > 1
                OR count(DISTINCT o.encounter_id) + max(CASE WHEN o.encounter_id IS NULL THEN 1 ELSE 0 END) > 1
                OR count(*) FILTER (WHERE o.status <> 'entered-in-error') NOT IN (0, 2)) THEN
            RAISE EXCEPTION 'A blood pressure reading is a systolic and a diastolic of one patient, time and visit, standing together or entered in error together'
                USING ERRCODE = '23514';
        END IF;
    END LOOP;
    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.check_vital_reading() FROM PUBLIC;
-- A constraint trigger can't be replaced in place: dropped and made again, in one transaction, so no commit passes
-- between the two unchecked (the drop waits for any transaction with rows written).
BEGIN;
DROP TRIGGER IF EXISTS observations_reading_inserted ON ehr.observations;
CREATE CONSTRAINT TRIGGER observations_reading_inserted
    AFTER INSERT ON ehr.observations
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW WHEN (NEW.reading_id IS NOT NULL)
    EXECUTE FUNCTION ehr.check_vital_reading();
DROP TRIGGER IF EXISTS observations_reading_changed ON ehr.observations;
CREATE CONSTRAINT TRIGGER observations_reading_changed
    AFTER UPDATE ON ehr.observations
    DEFERRABLE INITIALLY DEFERRED
    -- Only when what the check reads changes: the reading, its patient, time or visit, or a value's status or code.
    -- An update of anything else, such as a migration giving readings a new reference range, has nothing to check
    -- and leaves nothing waiting for the commit, so the same transaction can still alter the table to lift and put
    -- back its protection (PostgreSQL refuses to alter a table with checks still to run).
    FOR EACH ROW WHEN ((OLD.reading_id IS NOT NULL OR NEW.reading_id IS NOT NULL)
        AND (OLD.reading_id, OLD.patient_id, OLD.effective_at, OLD.encounter_id, OLD.status, OLD.code)
            IS DISTINCT FROM (NEW.reading_id, NEW.patient_id, NEW.effective_at, NEW.encounter_id, NEW.status, NEW.code))
    EXECUTE FUNCTION ehr.check_vital_reading();
DROP TRIGGER IF EXISTS observations_reading_deleted ON ehr.observations;
CREATE CONSTRAINT TRIGGER observations_reading_deleted
    AFTER DELETE ON ehr.observations
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW WHEN (OLD.reading_id IS NOT NULL)
    EXECUTE FUNCTION ehr.check_vital_reading();
COMMIT;

-- A visit the apps insert is a check-in (Appointments.CheckInAsync, the only one): it starts when it is inserted.
-- Before the insert, as the caller, for the apps' roles: its start is the database's wall clock, whatever was sent, as
-- an appointment's booked_at, a result's issued_at and an acknowledgment's time are (step 9c's follow-up, after its
-- review). Not the transaction's start, which a session holding its transaction open could make as early as it liked.
-- So a visit the apps add never starts in the past or the future, and no visit dated to suit can take a vital sign. Any
-- status but in progress is refused: the check-in always sends in progress, so the pages never meet the refusal (the
-- page turns it into its "Not saved" if one ever comes). The seed and the migrations insert visits as the owner, and
-- are not held to it.
CREATE OR REPLACE FUNCTION ehr.check_in_visit() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF NEW.status IS DISTINCT FROM 'in-progress' THEN
        RAISE EXCEPTION 'A visit is started by checking the patient in, in progress' USING ERRCODE = '23514';
    END IF;
    NEW.period_start := clock_timestamp();
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.check_in_visit() FROM PUBLIC;
CREATE OR REPLACE TRIGGER encounters_check_in
    BEFORE INSERT ON ehr.encounters
    -- For a visit the apps insert, by the role they connect as, as for filing (observations_file above).
    FOR EACH ROW WHEN (current_user IN ('ehr_app', 'ehr_read'))
    EXECUTE FUNCTION ehr.check_in_visit();

-- A check-in ties the vital signs already recorded to the visit it starts, exactly when the rule ehr.file_result decides
-- a moment's visit by (above) now gives this one (Tahseen's answer on step 9c): a moment taken from fifteen minutes
-- before the visit's start to twelve hours after it, the same window, that has no visit or the visit of one this one
-- wins over: one that began earlier, or at the same instant with a greater ID, the order ehr.file_result picks by. The
-- start is the wall clock at the insert (ehr.check_in_visit, above), so the window is the clock's. So a sign's visit
-- depends on the time it was taken alone, whether it was typed before the check-in or after it. A moment moves whole,
-- every value of it, standing or entered in error, so a reading is never split, and its reading check
-- (ehr.check_vital_reading) runs at the commit as for any change of visit. Only the visit's own patient's vital signs,
-- and only their visit: nothing else changes. After the insert of a visit the apps make, by a clinician with the patient
-- on their panel (30-rls.sql). As the tables' owner, since the apps may not change an observation's visit
-- (10-grants.sql); the owner's own policy lets it (observations_owner_update, 30-rls.sql). Each value moved is a change
-- of its visit, audited as any write is, as the person checking the patient in. It waits for any transaction recording
-- a value for the patient, and they for it (the lock ehr.file_result takes), so none is missed. After it, nothing the
-- apps do moves the moment again but another check-in within fifteen minutes after it was taken, which the rule gives
-- the moment to; never back to a visit that began earlier.
CREATE OR REPLACE FUNCTION ehr.attach_vital_signs() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    -- As for filing (above): under a snapshot taken earlier, the update could miss a value committed since.
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'A visit is started in a READ COMMITTED transaction' USING ERRCODE = '0A000';
    END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended('ehr.encounters:' || NEW.patient_id::text, 0));

    UPDATE ehr.observations o SET encounter_id = NEW.id
    WHERE o.patient_id = NEW.patient_id AND o.category = 'vital-signs'
      AND o.effective_at >= NEW.period_start - interval '15 minutes' AND o.effective_at <= NEW.period_start + interval '12 hours'
      AND (o.encounter_id IS NULL
           OR EXISTS (SELECT 1 FROM ehr.encounters e
                      WHERE e.id = o.encounter_id
                        AND (e.period_start < NEW.period_start OR (e.period_start = NEW.period_start AND e.id > NEW.id))));
    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.attach_vital_signs() FROM PUBLIC;
CREATE OR REPLACE TRIGGER encounters_attach_vital_signs
    AFTER INSERT ON ehr.encounters
    -- For a check-in the apps make (ehr.check_in_visit holds what that is), as for filing (observations_file above).
    FOR EACH ROW WHEN (current_user IN ('ehr_app', 'ehr_read') AND NEW.status = 'in-progress')
    EXECUTE FUNCTION ehr.attach_vital_signs();

CREATE OR REPLACE TRIGGER observations_complete_order
    AFTER INSERT ON ehr.observations
    FOR EACH ROW WHEN (NEW.order_id IS NOT NULL AND NEW.status = 'final')
    EXECUTE FUNCTION ehr.complete_resulted_order();

-- After an order becomes completed: the clinician who signed it gets an inbox item, "Results are in", naming the
-- patient and the order and none of the values. If that account can no longer sign in or is no longer a clinician,
-- the item goes to each clinician with the patient on their panel instead, so results never arrive unseen.
-- SECURITY DEFINER, because no runtime role may insert inbox items (10-grants.sql); beyond the order's own row it
-- reads the signer's account and, for the fallback, the patient's panel.
CREATE OR REPLACE FUNCTION ehr.deliver_result() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM ehr.staff_users s
               WHERE s.id = NEW.signer_id AND s.roles @> ARRAY['Clinician'] AND (s.lockout_end IS NULL OR s.lockout_end <= now())) THEN
        INSERT INTO ehr.inbox_items (id, recipient_id, kind, patient_id, source_table, source_id, summary, created_at)
        VALUES (uuidv7(), NEW.signer_id, 'result', NEW.patient_id, 'orders', NEW.id, 'Results are in', clock_timestamp());
    ELSE
        INSERT INTO ehr.inbox_items (id, recipient_id, kind, patient_id, source_table, source_id, summary, created_at)
        SELECT uuidv7(), a.clinician_id, 'result', NEW.patient_id, 'orders', NEW.id, 'Results are in', clock_timestamp()
        FROM ehr.patient_assignments a
        JOIN ehr.staff_users s ON s.id = a.clinician_id
        WHERE a.patient_id = NEW.patient_id AND a.ended_at IS NULL
          AND s.roles @> ARRAY['Clinician'] AND (s.lockout_end IS NULL OR s.lockout_end <= now());
    END IF;

    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.deliver_result() FROM PUBLIC;
CREATE OR REPLACE TRIGGER orders_deliver_result
    AFTER UPDATE ON ehr.orders
    FOR EACH ROW WHEN (OLD.status = 'active' AND NEW.status = 'completed')
    EXECUTE FUNCTION ehr.deliver_result();

-- Before an acknowledgment is inserted: who and when are the database's, and the order is a completed order of the
-- same patient, which means it has results. Runs as the caller. Once for each order (a unique index).
CREATE OR REPLACE FUNCTION ehr.acknowledge_results() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'Results are acknowledged in a READ COMMITTED transaction' USING ERRCODE = '0A000';
    END IF;

    PERFORM 1 FROM ehr.orders o WHERE o.id = NEW.order_id AND o.patient_id = NEW.patient_id AND o.status = 'completed' FOR NO KEY UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Only an order whose results are in is acknowledged' USING ERRCODE = '23514';
    END IF;

    NEW.acknowledged_by := ehr.current_user_id();
    NEW.acknowledged_at := clock_timestamp();
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.acknowledge_results() FROM PUBLIC;
CREATE OR REPLACE TRIGGER result_acknowledgments_guard
    BEFORE INSERT ON ehr.result_acknowledgments
    FOR EACH ROW EXECUTE FUNCTION ehr.acknowledge_results();

-- After an acknowledgment: the order's inbox items are done, whoever's they are. SECURITY DEFINER, as an item is
-- otherwise marked done only by its recipient.
CREATE OR REPLACE FUNCTION ehr.clear_result_items() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    UPDATE ehr.inbox_items SET done_at = clock_timestamp()
    WHERE source_table = 'orders' AND source_id = NEW.order_id AND kind = 'result' AND done_at IS NULL;
    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.clear_result_items() FROM PUBLIC;
CREATE OR REPLACE TRIGGER result_acknowledgments_clear_items
    AFTER INSERT ON ehr.result_acknowledgments
    FOR EACH ROW EXECUTE FUNCTION ehr.clear_result_items();
