-- Results of an order, and their acknowledgment (the expansion plan's step 7): what the database itself holds them
-- to, whatever the app does. Who may file and acknowledge is row-level security's (30-rls.sql): a clinician with the
-- patient on their active panel. Plain SQL, safe to run again.

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
BEGIN
    -- As for signing an order (60-orders.sql): under a snapshot taken earlier, the checks below could miss an
    -- acknowledgment that has since been committed.
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'A result is filed in a READ COMMITTED transaction' USING ERRCODE = '0A000';
    END IF;

    -- A vital sign (step 9) answers no order. It is one of the vital-sign codes, taken no later than now, at a visit of
    -- the same patient when it names one; its name, unit and reference range are the database's own, as for a result;
    -- it is final and released at once. A body mass index is never the client's number: it is worked out here from the
    -- weight and height recorded for the same moment, and refused without them.
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

        -- The visit it was taken at. Named: a visit of the same patient that was held. Not named: the patient's visit
        -- that was held and began in the twelve hours before it was taken (or within fifteen minutes after), the
        -- latest of them, if there is one; the chart and the offline copy show a visit's vital signs with the visit.
        IF NEW.encounter_id IS NOT NULL THEN
            IF NOT EXISTS (SELECT 1 FROM ehr.encounters e
                           WHERE e.id = NEW.encounter_id AND e.patient_id = NEW.patient_id
                             AND e.status NOT IN ('planned', 'cancelled', 'entered-in-error')) THEN
                RAISE EXCEPTION 'A vital sign is taken at a visit of the same patient that was held' USING ERRCODE = '23514';
            END IF;
        ELSE
            SELECT e.id INTO NEW.encounter_id
            FROM ehr.encounters e
            WHERE e.patient_id = NEW.patient_id AND e.status NOT IN ('planned', 'cancelled', 'entered-in-error')
              AND e.period_start <= NEW.effective_at + interval '15 minutes'
              AND e.period_start >= NEW.effective_at - interval '12 hours'
            ORDER BY e.period_start DESC, e.id
            LIMIT 1;
        END IF;

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
        -- Heart rate, respiratory rate and blood pressure have a range for each age band (step 9b): the patient's age
        -- on the day the sign was taken picks it. A sign with no banded range keeps the one above.
        SELECT r.low, r.high INTO NEW.reference_low, NEW.reference_high
        FROM ehr.vital_reference_ranges r
        JOIN ehr.patients p ON p.id = NEW.patient_id
        CROSS JOIN LATERAL (
            -- Between two timestamps without a zone, so the session's time zone can't move a month boundary.
            SELECT (extract(year FROM age((NEW.effective_at AT TIME ZONE 'UTC')::date::timestamp, p.birth_date::timestamp)) * 12
                    + extract(month FROM age((NEW.effective_at AT TIME ZONE 'UTC')::date::timestamp, p.birth_date::timestamp)))::int AS months) a
        WHERE r.code = NEW.code AND greatest(a.months, 0) >= r.from_months AND (r.to_months IS NULL OR greatest(a.months, 0) < r.to_months);
        IF NOT FOUND THEN
            -- A sign that has banded ranges but none for this age (a diastolic pressure under 1 year) has no range,
            -- rather than the adults'. A sign with no banded ranges keeps the one above.
            IF EXISTS (SELECT 1 FROM ehr.vital_reference_ranges r WHERE r.code = NEW.code) THEN
                NEW.reference_low := NULL;
                NEW.reference_high := NULL;
            ELSE
                NEW.reference_low := test.reference_low;
                NEW.reference_high := test.reference_high;
            END IF;
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
    -- which a statement can change between its own rows. Rows loaded by the schema's owner (the seed) or a superuser
    -- (a test standing in for a lab interface) don't pass through here.
    FOR EACH ROW WHEN (current_user IN ('ehr_app', 'ehr_read'))
    EXECUTE FUNCTION ehr.file_result();

-- After a result is inserted: its order, if still active, becomes completed. As the caller, who may update the orders
-- of a patient on their panel; the orders' own trigger allows exactly this change (60-orders.sql).
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
