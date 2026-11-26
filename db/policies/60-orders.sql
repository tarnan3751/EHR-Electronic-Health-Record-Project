-- UC 1 Place an order (Tahseen Arnan): signing an order, and discontinuing or changing one. Applied as ehr_owner after
-- migrations, on every run of infra/migrate; safe to re-run. The functions run as the caller, not as their owner, so
-- row-level security and the grants bind what they read and lock, as they bind the write itself (30-rls.sql: a
-- clinician, as themselves, for a patient on their panel). There is no DELETE grant on orders, and the only UPDATE
-- grant is on the three columns discontinuing writes (10-grants.sql): a signed order is never edited in place.
--
-- Before each insert, ehr.sign_order():
--   0. refuses any transaction that isn't READ COMMITTED, the level page handlers run at. Under REPEATABLE READ or
--      SERIALIZABLE the transaction that waited at the lock would still check for a duplicate against its old
--      snapshot, miss the order it waited for, and let both through.
--   1. locks the patient's row, FOR NO KEY UPDATE, so one patient's orders are signed one at a time: a second
--      transaction waits here until the first ends, and then sees its order. The lock needs the UPDATE on patients that
--      ehr_app has. Foreign keys to the patient take a weaker lock, which this one doesn't block, and plain reads take
--      none, so only another order (or an update of the patient) waits. Anyone but a clinician sees no patient, and is
--      refused here.
--   2. refuses an order that isn't active: an order is signed active, and only discontinuing it or its results
--      arriving changes that.
--   3. for a replacement (replaces_order_id), refuses one whose replaced order isn't an active order of the same
--      patient and type. The change is finished by discontinuing that order in the same transaction, which
--      ehr.check_order_replacement() checks at commit.
--   4. refuses an active order for an item the patient already has an active order for, unless it gives a reason. The
--      order a replacement replaces doesn't count: it is discontinued in the same transaction. Two submissions at once
--      can't both get through: the second waited at the lock. The error is unique_violation (23505), which
--      Ehr.Domain.Orders.ClinicalOrders turns into the order form's warning.
--   5. sets the signing time to the clock's, as signing a note does, whatever the client sent: an order that waited
--      at the lock is signed after the one it waited for. The signer is the column's default, the
--      signed-in user, and the row-level security policy refuses any other.
CREATE OR REPLACE FUNCTION ehr.sign_order() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'An order is signed in a READ COMMITTED transaction' USING ERRCODE = '0A000';
    END IF;

    PERFORM 1 FROM ehr.patients WHERE id = NEW.patient_id FOR NO KEY UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Only a clinician may sign an order for this patient' USING ERRCODE = '42501';
    END IF;

    IF NEW.status IS DISTINCT FROM 'active' THEN
        RAISE EXCEPTION 'An order is signed active' USING ERRCODE = '23514';
    END IF;

    IF NEW.replaces_order_id IS NOT NULL THEN
        PERFORM 1 FROM ehr.orders o
        WHERE o.id = NEW.replaces_order_id AND o.patient_id = NEW.patient_id AND o.type = NEW.type AND o.status = 'active';
        IF NOT FOUND THEN
            RAISE EXCEPTION 'An order replaces an active order of the same patient and type' USING ERRCODE = '23514';
        END IF;
    END IF;

    IF coalesce(NEW.duplicate_reason, '') !~ '[^[:space:]]'
       AND EXISTS (
           SELECT 1 FROM ehr.orders o
           WHERE o.patient_id = NEW.patient_id AND o.catalog_code = NEW.catalog_code AND o.status = 'active'
             AND o.id IS DISTINCT FROM NEW.replaces_order_id) THEN
        RAISE EXCEPTION 'The patient already has an active order for this item; give a reason to order it again'
            USING ERRCODE = '23505';
    END IF;

    -- Ordering safety checks (step 8, 61-order-safety.sql): what is known against a medication order is found here,
    -- whatever the client sent, and kept on the order. When any of it is serious (an allergy, a drug of a class known
    -- to cross-react with one the patient is allergic to, a major or high-priority interaction), the order is signed
    -- only with a reason for going ahead, which stays on the order beside what was found; both are in the write audit with the order. A moderate
    -- or minor interaction is shown and kept, and doesn't stop the order: a reason demanded for everything is a
    -- reason typed without reading.
    NEW.safety_warnings := NULL;
    IF NEW.type = 'medication' THEN
        -- A medication marked sensitive is not named in what is kept: an order's audit details are open to admins.
        SELECT jsonb_agg(jsonb_build_object('kind', w.kind, 'severity', w.severity, 'ordered', w.ordered,
                             'against', CASE WHEN w.against_sensitive THEN 'a medication marked sensitive' ELSE w.against END, 'source', w.source)
                         ORDER BY w.kind, w.against, w.source)
        INTO NEW.safety_warnings
        FROM ehr.order_safety_warnings(NEW.patient_id, NEW.catalog_code, NEW.replaces_order_id) w;
    END IF;

    IF NEW.safety_warnings IS NULL
       OR NOT jsonb_path_exists(NEW.safety_warnings,
              '$[*] ? (@.kind == "allergy" || @.source == "cross-reactivity" || @.severity == "major" || @.severity == "high-priority")') THEN
        -- Nothing serious was found, so there is nothing to override.
        NEW.safety_override_reason := NULL;
    ELSIF coalesce(NEW.safety_override_reason, '') !~ '[^[:space:]]' THEN
        RAISE EXCEPTION 'This order has safety warnings; give a reason to sign it all the same'
            USING ERRCODE = '23514', CONSTRAINT = 'orders_safety_override';
    END IF;

    NEW.signed_at := clock_timestamp();
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.sign_order() FROM PUBLIC;
CREATE OR REPLACE TRIGGER orders_sign
    BEFORE INSERT ON ehr.orders
    FOR EACH ROW EXECUTE FUNCTION ehr.sign_order();

-- Discontinuing an order: the one update a person makes (the other, completing it when results arrive, is below). Row-level security lets a clinician update only the orders of a
-- patient on their panel, and the grant covers only status, discontinue_reason and replaced_by_order_id; this trigger
-- holds the update to exactly one change, whoever makes it:
--   1. only an active order, and only to revoked (FHIR's code for a stopped request, shown as "Discontinued"). A
--      second discontinuation waits for the first at the row's lock, then finds the order revoked and is refused, so
--      nothing reactivates an order or discontinues it twice.
--   2. no other column changes: not the patient, the item, the details, the signer, the signing time, the duplicate
--      reason or the order it replaced.
--   3. a reason is required.
--   4. when it names the order that replaced it, that order is an active order of the same patient that names this
--      one, signed by the same clinician: the links always point both ways.
--   5. who and when are the database's: the signed-in user and the clock, whatever the client sent.
CREATE OR REPLACE FUNCTION ehr.discontinue_order() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    -- The other change an order takes (step 7): an active order becomes completed, FHIR's code for a request that
    -- has been carried out and shown as "Resulted", once a final result has been filed for it, and nothing else on
    -- the row moves. The database makes this change itself when the first result arrives (75-results.sql).
    IF OLD.status = 'active' AND NEW.status = 'completed' THEN
        PERFORM 1 FROM ehr.observations o WHERE o.order_id = OLD.id AND o.patient_id = OLD.patient_id AND o.status = 'final';
        IF NOT FOUND THEN
            RAISE EXCEPTION 'An order is completed by its results' USING ERRCODE = '23514';
        END IF;

        IF (to_jsonb(NEW) - 'status') IS DISTINCT FROM (to_jsonb(OLD) - 'status') THEN
            RAISE EXCEPTION 'A signed order is never edited' USING ERRCODE = '42501';
        END IF;

        RETURN NEW;
    END IF;

    IF OLD.status IS DISTINCT FROM 'active' OR NEW.status IS DISTINCT FROM 'revoked' THEN
        RAISE EXCEPTION 'An order changes only once, from active to discontinued' USING ERRCODE = '23514';
    END IF;

    IF (NEW.id, NEW.patient_id, NEW.type, NEW.catalog_code, NEW.details, NEW.signer_id, NEW.signed_at, NEW.duplicate_reason, NEW.replaces_order_id,
        NEW.safety_warnings, NEW.safety_override_reason)
       IS DISTINCT FROM
       (OLD.id, OLD.patient_id, OLD.type, OLD.catalog_code, OLD.details, OLD.signer_id, OLD.signed_at, OLD.duplicate_reason, OLD.replaces_order_id,
        OLD.safety_warnings, OLD.safety_override_reason) THEN
        RAISE EXCEPTION 'A signed order is never edited' USING ERRCODE = '42501';
    END IF;

    IF coalesce(NEW.discontinue_reason, '') !~ '[^[:space:]]' THEN
        RAISE EXCEPTION 'Discontinuing an order needs a reason' USING ERRCODE = '23514';
    END IF;

    IF NEW.replaced_by_order_id IS NOT NULL THEN
        PERFORM 1 FROM ehr.orders r
        WHERE r.id = NEW.replaced_by_order_id AND r.replaces_order_id = OLD.id AND r.patient_id = OLD.patient_id
          AND r.status = 'active' AND r.signer_id = ehr.current_user_id();
        IF NOT FOUND THEN
            RAISE EXCEPTION 'An order is replaced by the order that names it, signed by the same clinician' USING ERRCODE = '23514';
        END IF;
    END IF;

    NEW.discontinuer_id := ehr.current_user_id();
    NEW.discontinued_at := clock_timestamp();
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.discontinue_order() FROM PUBLIC;
CREATE OR REPLACE TRIGGER orders_discontinue
    BEFORE UPDATE ON ehr.orders
    FOR EACH ROW EXECUTE FUNCTION ehr.discontinue_order();

-- A change is whole or it didn't happen: checked when the transaction commits, an order that replaces another finds
-- that order discontinued and naming it. A replacement signed without discontinuing the order it replaces fails here,
-- and takes its transaction with it.
CREATE OR REPLACE FUNCTION ehr.check_order_replacement() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    PERFORM 1 FROM ehr.orders o
    WHERE o.id = NEW.replaces_order_id AND o.status = 'revoked' AND o.replaced_by_order_id = NEW.id;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'An order that replaces another discontinues it in the same transaction' USING ERRCODE = '23514';
    END IF;

    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.check_order_replacement() FROM PUBLIC;
-- A constraint trigger can't be replaced in place. Dropped and created in one transaction, so no replacement signed
-- while this file runs again commits without the check.
BEGIN;
DROP TRIGGER IF EXISTS orders_replacement ON ehr.orders;
CREATE CONSTRAINT TRIGGER orders_replacement
    AFTER INSERT ON ehr.orders
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW WHEN (NEW.replaces_order_id IS NOT NULL)
    EXECUTE FUNCTION ehr.check_order_replacement();
COMMIT;
