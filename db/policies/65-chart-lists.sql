-- Editable allergies, medications and diagnoses (UC 4; the expansion plan's step 2). Applied as ehr_owner after
-- migrations, on every run of infra/migrate; safe to re-run. The grants are in 10-grants.sql and the row-level security
-- policies in 30-rls.sql: a clinician adds and changes entries only for a patient on their active panel, and nothing
-- deletes one.

-- Whether an ICD-10-CM code is in a sensitive range. The rule, recorded in docs/expansion/PLAN.md's decision log:
--   F01-F99           mental, behavioral and neurodevelopmental disorders, substance use (F10-F19) included
--   A50-A64           infections with a predominantly sexual mode of transmission
--   B20, Z21          HIV disease, and asymptomatic HIV infection status
--   O04, Z33.2        termination of pregnancy
--   T74, T76          abuse, neglect and other maltreatment, confirmed or suspected
--   X71-X83           intentional self-harm
--   T14.91            suicide attempt
--   R45.851           suicidal ideations
--   R45.88            nonsuicidal self-harm
--   Z91.51, Z91.52    personal history of suicidal behavior, and of nonsuicidal self-harm
--   T71.1x2, T71.2x2  asphyxiation, intentional self-harm: the T71.1 and T71.2 codes whose sixth character is 2
--                     (T71.162A). Checked against the fiscal year 2027 code file: exactly its 24 "intentional
--                     self-harm" codes in T71, and no others.
--   T36-T65, intent 2 poisoning by drugs and toxic effects of other substances, intentional self-harm: the codes whose
--                     sixth character is 2 (T39.012A), and those of a four-character subcategory, where the 2 is
--                     the fifth and an X fills the sixth (T36.92XA). Checked against the fiscal year 2027 code file:
--                     together these are exactly its 954 "intentional self-harm" codes in T36-T65, and no others.
-- Codes are compared without regard to case or surrounding spaces, with or without the dot. Not a table: the rule
-- changes by review here.
CREATE OR REPLACE FUNCTION ehr.icd10cm_is_sensitive(code text) RETURNS boolean
    LANGUAGE sql
    IMMUTABLE
    PARALLEL SAFE
    SET search_path = pg_catalog, pg_temp
AS $$
    SELECT coalesce(
        c ~ '^F[0-9][0-9A-Z]'
        OR (c ~ '^A[0-9]{2}' AND substr(c, 1, 3) BETWEEN 'A50' AND 'A64')
        OR c ~ '^(B20|Z21|O04|Z332|T74|T76|R45851|R4588|T1491|Z9151|Z9152)'
        OR (c ~ '^X[0-9]{2}' AND substr(c, 1, 3) BETWEEN 'X71' AND 'X83')
        OR (c ~ '^T[0-9]{2}' AND substr(c, 1, 3) BETWEEN 'T36' AND 'T65' AND (substr(c, 6, 1) = '2' OR substr(c, 5, 2) = '2X'))
        OR c ~ '^T71[12][0-9]2',
        false)
    FROM (SELECT upper(replace(btrim(code), '.', '')) AS c) AS normalized
$$;
REVOKE ALL ON FUNCTION ehr.icd10cm_is_sensitive(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ehr.icd10cm_is_sensitive(text) TO ehr_app, ehr_read;

-- Before each insert or update of a chart entry. Runs as the caller, so the grants and row-level security bind it.
--   1. An entry never moves: not to another patient, and not to another ID.
--   2. A diagnosis whose code is in a sensitive range is sensitive, whatever the client sent: the app can't clear it.
--   3. "No known allergies" is refused for a patient who has an active allergy recorded: the list is one or the other.
--   4. A second active "No known allergies" is refused, as a unique violation naming the index that also holds it
--      (ix_allergy_intolerances_one_no_known_allergy), so the app treats it as already recorded.
CREATE OR REPLACE FUNCTION ehr.guard_chart_entry() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND (NEW.id IS DISTINCT FROM OLD.id OR NEW.patient_id IS DISTINCT FROM OLD.patient_id) THEN
        RAISE EXCEPTION 'A chart entry stays with its patient' USING ERRCODE = '42501';
    END IF;

    IF TG_TABLE_NAME = 'conditions' AND ehr.icd10cm_is_sensitive(NEW.code) THEN
        NEW.sensitive := true;
    END IF;

    -- Nested, not one condition: the other two tables have no clinical_status for the row's fields to resolve.
    IF TG_TABLE_NAME = 'allergy_intolerances' THEN
        -- One patient's allergy entries are written one at a time: this locks the patient's row, as signing an order
        -- does (60-orders.sql), so a second transaction waits here until the first ends, and then sees what it wrote.
        -- Without it, "No known allergies" and an allergy recorded at the same moment would each miss the other, and
        -- both stay active. A row lock, not an advisory lock: those are held in shared memory, one per patient, until
        -- the transaction ends, and a transaction that loads many patients runs out.
        PERFORM 1 FROM ehr.patients WHERE id = NEW.patient_id FOR NO KEY UPDATE;
        IF NEW.code = 'no-known-allergy' AND NEW.clinical_status = 'active'
           AND EXISTS (
               SELECT 1 FROM ehr.allergy_intolerances a
               WHERE a.patient_id = NEW.patient_id AND a.clinical_status = 'active' AND a.code IS DISTINCT FROM 'no-known-allergy'
                 AND a.id <> NEW.id) THEN
            RAISE EXCEPTION 'A patient with an allergy recorded has no "No known allergies"' USING ERRCODE = '23514';
        END IF;
        -- After the lock, so a double click's second insert sees the first's row.
        IF NEW.code = 'no-known-allergy' AND NEW.clinical_status = 'active'
           AND EXISTS (
               SELECT 1 FROM ehr.allergy_intolerances a
               WHERE a.patient_id = NEW.patient_id AND a.clinical_status = 'active' AND a.code = 'no-known-allergy' AND a.id <> NEW.id) THEN
            RAISE EXCEPTION '"No known allergies" is already recorded for this patient'
                USING ERRCODE = '23505', CONSTRAINT = 'ix_allergy_intolerances_one_no_known_allergy';
        END IF;
    END IF;

    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.guard_chart_entry() FROM PUBLIC;

CREATE OR REPLACE TRIGGER allergy_intolerances_guard
    BEFORE INSERT OR UPDATE ON ehr.allergy_intolerances
    FOR EACH ROW EXECUTE FUNCTION ehr.guard_chart_entry();
CREATE OR REPLACE TRIGGER medication_statements_guard
    BEFORE INSERT OR UPDATE ON ehr.medication_statements
    FOR EACH ROW EXECUTE FUNCTION ehr.guard_chart_entry();
CREATE OR REPLACE TRIGGER conditions_guard
    BEFORE INSERT OR UPDATE ON ehr.conditions
    FOR EACH ROW EXECUTE FUNCTION ehr.guard_chart_entry();

-- A patient's allergy list is one of three things: nothing recorded, "No known allergies", or allergies. So when an
-- active allergy is added, or an entry is made active again, an active "No known allergies" is made inactive, in the
-- same statement's transaction, by whoever recorded the allergy. Runs as the caller: the update is theirs, bound by
-- the same policies, and goes into the history and the write audit like any other.
CREATE OR REPLACE FUNCTION ehr.clear_no_known_allergies() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    UPDATE ehr.allergy_intolerances
    SET clinical_status = 'inactive'
    WHERE patient_id = NEW.patient_id AND code = 'no-known-allergy' AND clinical_status = 'active';
    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.clear_no_known_allergies() FROM PUBLIC;
CREATE OR REPLACE TRIGGER allergy_intolerances_status
    AFTER INSERT OR UPDATE ON ehr.allergy_intolerances
    FOR EACH ROW WHEN (NEW.clinical_status = 'active' AND NEW.code IS DISTINCT FROM 'no-known-allergy')
    EXECUTE FUNCTION ehr.clear_no_known_allergies();

-- The history clinicians read: after each insert or update of a chart entry, who changed it, when, and the entry
-- before and after. It runs as its owner, ehr_owner (SECURITY DEFINER), because nothing at runtime may write
-- ehr.chart_history: so the history can't be skipped, forged or edited. Its search_path is pinned, and nobody but its
-- owner may call it directly. An update that changes nothing adds nothing. The write audit (40-audit.sql) records the
-- same changes for admins; this is the copy the care team sees beside the chart.
CREATE OR REPLACE FUNCTION ehr.record_chart_history() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND to_jsonb(NEW) = to_jsonb(OLD) THEN
        RETURN NULL;
    END IF;

    INSERT INTO ehr.chart_history (id, patient_id, table_name, row_id, changed_at, changed_by_id, before, after)
    VALUES (
        uuidv7(),
        NEW.patient_id,
        TG_TABLE_NAME,
        NEW.id,
        clock_timestamp(),
        NULLIF(current_setting('app.user_id', true), '')::uuid,
        CASE WHEN TG_OP = 'UPDATE' THEN to_jsonb(OLD) END,
        to_jsonb(NEW));
    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.record_chart_history() FROM PUBLIC;

CREATE OR REPLACE TRIGGER allergy_intolerances_history
    AFTER INSERT OR UPDATE ON ehr.allergy_intolerances
    FOR EACH ROW EXECUTE FUNCTION ehr.record_chart_history();
CREATE OR REPLACE TRIGGER medication_statements_history
    AFTER INSERT OR UPDATE ON ehr.medication_statements
    FOR EACH ROW EXECUTE FUNCTION ehr.record_chart_history();
CREATE OR REPLACE TRIGGER conditions_history
    AFTER INSERT OR UPDATE ON ehr.conditions
    FOR EACH ROW EXECUTE FUNCTION ehr.record_chart_history();

-- Diagnoses recorded before this rule, or before a range was added to it, are marked now: the trigger above only sees
-- rows as they are written. Run on every pass, so widening the rule reaches the rows already there. Forced row-level
-- security hides every row from this role, so it is lifted for the statement and put back in the same transaction, as
-- the synthetic seed does; no other session sees the table without it. The update goes through the triggers like any
-- other: the history and the write audit show the change, made by nobody signed in.
BEGIN;
ALTER TABLE ehr.conditions NO FORCE ROW LEVEL SECURITY;
UPDATE ehr.conditions SET sensitive = true WHERE NOT sensitive AND ehr.icd10cm_is_sensitive(code);
ALTER TABLE ehr.conditions FORCE ROW LEVEL SECURITY;
COMMIT;
