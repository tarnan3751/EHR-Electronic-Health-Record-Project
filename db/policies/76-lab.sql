-- The lab's door (the expansion plan's step 12, UC 20): how a lab's system, holding a key of its own, is handed the lab
-- orders waiting for results and files their results. The apps reach it only through these functions, from the
-- on-prem app's /api/lab (Ehr.Web's Api/Lab). Applied as ehr_owner after migrations, on every run of infra/migrate; safe
-- to run again.
--
-- No account stands behind a lab, so row-level security has no user to decide by. Each function is given the hash of
-- the lab's key and finds it in ehr.lab_keys, which no runtime role can read (the step 6c pattern: found by its hash
-- through the table's key, so there is nothing to compare afterwards), and refuses a session with anyone signed in. SQL
-- as an app's role without the hash is handed nothing and files nothing. The two functions that read or write patient
-- data run as the tables' owner, which forced row-level security binds too: the owner's own policies (30-rls.sql) let
-- them see lab orders only and diagnoses that aren't sensitive only, and insert laboratory results of an order only. A
-- result still passes through ehr.file_result (75-results.sql), as a clinician's does: its trigger fires for every
-- insert an app session makes, directly or through a function like these.

-- The lab's key, as its hash, replaced on every run of the migration job from EHR_LAB_KEY (infra/migrate/migrate.sh),
-- which works the hash out itself, so the key never reaches the database or its logs. None given: no lab, and the door
-- refuses every call. The owner's alone: no runtime role may run it.
CREATE OR REPLACE FUNCTION ehr.set_lab_key(hash text) RETURNS void
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    DELETE FROM ehr.lab_keys;
    IF coalesce(hash, '') <> '' THEN
        INSERT INTO ehr.lab_keys (key_hash) VALUES (lower(hash));
    END IF;
END
$$;
REVOKE ALL ON FUNCTION ehr.set_lab_key(text) FROM PUBLIC;

-- The door's one rule about who calls: the lab, by the hash of its key, with nobody signed in, so nothing it does is
-- ever an account's. Called by the functions below, as their owner; nobody else may run it.
CREATE OR REPLACE FUNCTION ehr.require_lab(lab_key text) RETURNS void
    LANGUAGE plpgsql
    STABLE
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF lab_key IS NULL OR NOT EXISTS (SELECT 1 FROM ehr.lab_keys k WHERE k.key_hash = lab_key)
       OR NULLIF(current_setting('app.user_id', true), '') IS NOT NULL THEN
        RAISE EXCEPTION 'Only the lab, by its key and with nobody signed in, uses this' USING ERRCODE = '42501';
    END IF;
END
$$;
REVOKE ALL ON FUNCTION ehr.require_lab(text) FROM PUBLIC;

-- Hands the lab the lab orders signed by transactions from `since` up to the watermark it returns, that are still
-- waiting for results: an active order, or one completed by results that came in part (step 7) and not yet
-- acknowledged, with each test of its catalog item that has no result yet. By transaction ID, from the change log
-- (20-change-log.sql), never by time, as sync does: a transaction that signs an order and commits late is still in a
-- later window. At most 100 transactions at once: when there were more, `more` is true and the watermark is the first
-- one left, for the next ask. A lab starting again asks from 0 and is handed every order still waiting.
--
-- For each order, only what a lab needs: its ID, when it was signed (the database's clock), and each waiting test's
-- LOINC code, unit, this clinic's reference range and how many decimals the range has, from ehr.observation_codes, so a
-- lab never keeps a copy of the ranges; and the codes of the patient's current diagnoses that aren't sensitive, so its
-- values can fit the patient. Never the patient's ID, name, MRN, birth date or sex, the order's details, medications or
-- allergies. A sensitive diagnosis is never seen here at all: the owner's policy on conditions leaves them out.
--
-- Every order handed over is a disclosure, recorded in the same statement: one audit event for each, 'sent_to_lab', the
-- order as its row, its patient, no account and the lab as the source. An order handed over again (a lab starting
-- again, a second lab) is recorded again.
CREATE OR REPLACE FUNCTION ehr.lab_orders(lab_key text, since xid8, OUT watermark xid8, OUT more boolean, OUT orders jsonb)
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    PERFORM ehr.require_lab(lab_key);

    -- One statement, so the watermark and the orders come from one snapshot: every transaction with a lower ID has
    -- finished, and its orders are visible here.
    WITH bound AS (
        SELECT pg_snapshot_xmin(pg_current_snapshot()) AS upto
    ),
    signed AS (
        SELECT DISTINCT l.transaction_id
        FROM ehr.change_log l, bound b
        WHERE l.table_name = 'orders' AND l.transaction_id >= since AND l.transaction_id < b.upto
        ORDER BY l.transaction_id
        LIMIT 101
    ),
    taken AS (
        SELECT s.transaction_id FROM signed s ORDER BY s.transaction_id LIMIT 100
    ),
    waiting AS (
        SELECT o.id, o.patient_id, o.signed_at, o.catalog_code, t.transaction_id
        FROM taken t
        JOIN ehr.change_log l ON l.table_name = 'orders' AND l.transaction_id = t.transaction_id
        JOIN ehr.orders o ON o.id = l.row_id
        WHERE o.type = 'lab' AND o.status IN ('active', 'completed')
          AND NOT EXISTS (SELECT 1 FROM ehr.result_acknowledgments a WHERE a.order_id = o.id)
    ),
    handed AS (
        SELECT w.id, w.patient_id, w.signed_at, w.transaction_id, tests.list AS tests, coalesce(diagnoses.list, '[]'::jsonb) AS conditions
        FROM waiting w
        CROSS JOIN LATERAL (
            SELECT jsonb_agg(jsonb_build_object(
                       'code', c.code, 'unit', c.unit, 'low', c.reference_low, 'high', c.reference_high,
                       'decimals', greatest(coalesce(scale(c.reference_low), 0), coalesce(scale(c.reference_high), 0)))
                       ORDER BY c.code) AS list
            FROM ehr.order_result_codes r
            JOIN ehr.observation_codes c ON c.code = r.observation_code
            WHERE r.catalog_code = w.catalog_code
              AND NOT EXISTS (SELECT 1 FROM ehr.observations x WHERE x.order_id = w.id AND x.code = c.code)
        ) tests
        CROSS JOIN LATERAL (
            SELECT jsonb_agg(DISTINCT d.code ORDER BY d.code) AS list
            FROM ehr.conditions d
            WHERE d.patient_id = w.patient_id AND d.code IS NOT NULL
              AND d.clinical_status IN ('active', 'recurrence', 'relapse') AND NOT d.sensitive
        ) diagnoses
        WHERE tests.list IS NOT NULL
    ),
    recorded AS (
        INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source, sensitive, details)
        SELECT uuidv7(), now(), NULL, 'sent_to_lab', 'orders', h.id, h.patient_id, 'lab', false, NULL
        FROM handed h
        RETURNING 1
    )
    SELECT
        CASE WHEN (SELECT count(*) FROM signed) > 100 THEN (SELECT max(s.transaction_id) FROM signed s)
             ELSE greatest(since, (SELECT b.upto FROM bound b)) END,
        (SELECT count(*) FROM signed) > 100,
        coalesce((SELECT jsonb_agg(jsonb_build_object('id', h.id, 'signedAt', h.signed_at, 'tests', h.tests, 'conditions', h.conditions)
                                   ORDER BY h.transaction_id, h.id)
                  FROM handed h), '[]'::jsonb)
    INTO watermark, more, orders;
END
$$;
REVOKE ALL ON FUNCTION ehr.lab_orders(text, xid8) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ehr.lab_orders(text, xid8) TO ehr_app;

-- Files the lab's results for one order, all or none, and says what happened in a word; it raises only for a caller
-- that isn't the lab. In this order:
--   malformed         not 1 to 50 results, a code given twice, or a value missing, negative, 10^8 or more, or with more
--                     than four decimals (the API refuses these before asking: this is for SQL of its own)
--   unknown-order     no lab order has that ID (the owner's policy shows lab orders alone, so any other order is unknown
--                     here too, and the lab learns nothing about it)
--   already-filed     every result posted is on file already, the same value collected at the same time: a post sent
--                     twice is answered the same and filed once
--   discontinued      the order was discontinued
--   closed            its results were acknowledged, or it is otherwise not open to results
--   not-reported      a test its catalog item doesn't report
--   wrong-unit        a unit that isn't the test's (results come in the unit the hand-over named)
--   in-the-future     collected after now, by more than five minutes of clock difference
--   different-result  a test already has a different result (a clinician's, entered by hand meanwhile): theirs stands
--   filed             the results not on file yet are filed
-- The order's row is locked first, so two posts for one order, an acknowledgment or a discontinuation go one at a time,
-- and the second post finds the first's results. The rows go through ehr.file_result as a clinician's do: the
-- database's name, unit and range, final and released at once; the order becomes completed and its signer is told
-- (ehr.deliver_result). Their write audit names no account, with the lab as the source (40-audit.sql).
CREATE OR REPLACE FUNCTION ehr.file_lab_results(lab_key text, lab_order uuid, collected_at timestamptz, codes text[], lab_values numeric[], units text[])
    RETURNS text
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    ordered ehr.orders%ROWTYPE;
    posted integer := coalesce(cardinality(codes), 0);
BEGIN
    PERFORM ehr.require_lab(lab_key);

    -- As for filing by hand (75-results.sql): under a snapshot taken earlier, the checks could miss what was committed
    -- while this waited for the order's lock.
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'A result is filed in a READ COMMITTED transaction' USING ERRCODE = '0A000';
    END IF;

    IF posted NOT BETWEEN 1 AND 50 OR collected_at IS NULL
       OR cardinality(lab_values) IS DISTINCT FROM posted OR cardinality(units) IS DISTINCT FROM posted
       OR (SELECT count(DISTINCT p.code) FROM unnest(codes) AS p(code)) <> posted
       OR EXISTS (SELECT 1 FROM unnest(codes, lab_values, units) AS p(code, value, unit)
                  WHERE p.code IS NULL OR p.value IS NULL OR p.unit IS NULL
                     OR p.value < 0 OR p.value >= 100000000 OR round(p.value, 4) <> p.value) THEN
        RETURN 'malformed';
    END IF;

    SELECT * INTO ordered FROM ehr.orders o WHERE o.id = lab_order FOR NO KEY UPDATE;
    IF NOT FOUND OR ordered.type <> 'lab' THEN
        RETURN 'unknown-order';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM unnest(codes, lab_values) AS p(code, value)
        WHERE NOT EXISTS (SELECT 1 FROM ehr.observations x
                          WHERE x.order_id = lab_order AND x.code = p.code AND x.value = p.value AND x.effective_at = collected_at)) THEN
        RETURN 'already-filed';
    END IF;

    IF ordered.status = 'revoked' THEN
        RETURN 'discontinued';
    END IF;

    IF ordered.status NOT IN ('active', 'completed') OR EXISTS (SELECT 1 FROM ehr.result_acknowledgments a WHERE a.order_id = lab_order) THEN
        RETURN 'closed';
    END IF;

    IF EXISTS (SELECT 1 FROM unnest(codes) AS p(code)
               WHERE NOT EXISTS (SELECT 1 FROM ehr.order_result_codes r WHERE r.catalog_code = ordered.catalog_code AND r.observation_code = p.code)) THEN
        RETURN 'not-reported';
    END IF;

    IF EXISTS (SELECT 1 FROM unnest(codes, units) AS p(code, unit)
               JOIN ehr.observation_codes c ON c.code = p.code
               WHERE c.unit IS DISTINCT FROM p.unit) THEN
        RETURN 'wrong-unit';
    END IF;

    IF collected_at > now() + interval '5 minutes' THEN
        RETURN 'in-the-future';
    END IF;

    IF EXISTS (SELECT 1 FROM unnest(codes, lab_values) AS p(code, value)
               JOIN ehr.observations x ON x.order_id = lab_order AND x.code = p.code
               WHERE x.value IS DISTINCT FROM p.value OR x.effective_at IS DISTINCT FROM collected_at) THEN
        RETURN 'different-result';
    END IF;

    -- Written as the lab, for these rows alone: the setting is the transaction's, and is cleared before returning.
    PERFORM set_config('app.write_source', 'lab', true);
    INSERT INTO ehr.observations (id, patient_id, order_id, category, code, display, status, value, unit, effective_at, reference_kind)
    SELECT uuidv7(), ordered.patient_id, lab_order, 'laboratory', p.code, p.code, 'final', p.value, p.unit, collected_at, 'normal'
    FROM unnest(codes, lab_values, units) AS p(code, value, unit)
    WHERE NOT EXISTS (SELECT 1 FROM ehr.observations x WHERE x.order_id = lab_order AND x.code = p.code);
    PERFORM set_config('app.write_source', '', true);
    RETURN 'filed';
END
$$;
REVOKE ALL ON FUNCTION ehr.file_lab_results(text, uuid, timestamptz, text[], numeric[], text[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ehr.file_lab_results(text, uuid, timestamptz, text[], numeric[], text[]) TO ehr_app;

-- Whether a lab is connected: a key is stored. Nothing else about it. The clinician's results page says so, on both
-- servers (the replica has the table too), and it decides nothing.
CREATE OR REPLACE FUNCTION ehr.lab_connected() RETURNS boolean
    LANGUAGE sql
    STABLE
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
    SELECT EXISTS (SELECT 1 FROM ehr.lab_keys)
$$;
REVOKE ALL ON FUNCTION ehr.lab_connected() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ehr.lab_connected() TO ehr_app, ehr_read;
