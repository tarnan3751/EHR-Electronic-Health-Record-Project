-- Results of an order, and their acknowledgment (the expansion plan's step 7): what the database itself holds them
-- to, whatever the app does. Who may file and acknowledge is row-level security's (30-rls.sql): a clinician with the
-- patient on their active panel. Plain SQL, safe to run again.

-- Before a result is inserted. Runs as the caller.
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
BEGIN
    -- As for signing an order (60-orders.sql): under a snapshot taken earlier, the checks below could miss an
    -- acknowledgment that has since been committed.
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'A result is filed in a READ COMMITTED transaction' USING ERRCODE = '0A000';
    END IF;

    IF NEW.order_id IS NULL THEN
        RAISE EXCEPTION 'A result is filed against the order that asked for it' USING ERRCODE = '23514';
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
