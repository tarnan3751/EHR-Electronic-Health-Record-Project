-- The write audit: every insert, update or delete of a row in a PHI table adds a row to ehr.audit_events, with the
-- actor from app.user_id, the row, its patient, and the values that changed. Applied as ehr_owner after migrations,
-- on every run of infra/migrate; safe to re-run. Reads are recorded by the PHI reader in Ehr.Domain, since
-- PostgreSQL has no SELECT trigger.
--
-- The function runs as its owner, ehr_owner (SECURITY DEFINER), so it can write the log whatever the caller may do.
-- Its search_path is pinned, so a caller can't put their own objects in front of the ones it uses, and nobody but
-- its owner may call it directly. Writes only ever happen on the primary, so the source is always onprem.

CREATE OR REPLACE FUNCTION ehr.record_write() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    old_row jsonb := CASE WHEN TG_OP = 'INSERT' THEN NULL ELSE to_jsonb(OLD) END;
    new_row jsonb := CASE WHEN TG_OP = 'DELETE' THEN NULL ELSE to_jsonb(NEW) END;
    kept jsonb := coalesce(new_row, old_row);
    changed jsonb;
BEGIN
    IF TG_OP = 'UPDATE' THEN
        -- Only the columns that changed, each with its old and new value.
        SELECT jsonb_object_agg(n.key, jsonb_build_object('old', o.value, 'new', n.value))
          INTO changed
          FROM jsonb_each(new_row) AS n JOIN jsonb_each(old_row) AS o ON o.key = n.key
         WHERE n.value IS DISTINCT FROM o.value;
    ELSE
        changed := jsonb_build_object(lower(TG_OP) || 'ed', kept);
    END IF;

    INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source, details)
    VALUES (
        uuidv7(),
        now(),
        NULLIF(current_setting('app.user_id', true), '')::uuid,
        lower(TG_OP),
        TG_TABLE_NAME,
        (kept ->> 'id')::uuid,
        -- The patient a row belongs to: the row itself in ehr.patients, the patient_id column in every other PHI table.
        CASE WHEN TG_TABLE_NAME = 'patients' THEN (kept ->> 'id')::uuid ELSE (kept ->> 'patient_id')::uuid END,
        'onprem',
        changed);
    RETURN NULL;
END
$$;

REVOKE ALL ON FUNCTION ehr.record_write() FROM PUBLIC;

-- Every PHI table gets this trigger; Ehr.Security.Tests fails for one that doesn't.
CREATE OR REPLACE TRIGGER patients_audit
    AFTER INSERT OR UPDATE OR DELETE ON ehr.patients
    FOR EACH ROW EXECUTE FUNCTION ehr.record_write();

CREATE OR REPLACE TRIGGER patient_assignments_audit
    AFTER INSERT OR UPDATE OR DELETE ON ehr.patient_assignments
    FOR EACH ROW EXECUTE FUNCTION ehr.record_write();

CREATE OR REPLACE TRIGGER break_glass_accesses_audit
    AFTER INSERT OR UPDATE OR DELETE ON ehr.break_glass_accesses
    FOR EACH ROW EXECUTE FUNCTION ehr.record_write();
