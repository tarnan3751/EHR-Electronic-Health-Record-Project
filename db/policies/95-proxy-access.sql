-- Proxy access (FR-19): what the database itself holds a proxy grant to, whatever the app does. Who may read, give
-- and revoke one is row-level security's (30-rls.sql); what a grant lets its proxy see is there too, in
-- ehr.current_patient_id and the functions beside it. Plain SQL, safe to run again.

-- Before a grant is inserted or updated. Runs as the caller.
--   Insert: the database sets who gave it and when. The email is stored as ASP.NET Core Identity normalizes it. It
--   expires in the future and within a year. A patient can't name themselves, and holds at most 20 grants that
--   aren't revoked. Only the patient allows sensitive items: a grant a clinician records never includes them.
--   Update: a revoked grant never changes. Revoking sets when and who. Whether sensitive items are included is
--   changed by the patient alone, as themselves.
CREATE OR REPLACE FUNCTION ehr.guard_proxy_grant() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        NEW.granted_by := ehr.current_user_id();
        NEW.granted_at := clock_timestamp();
        NEW.revoked_at := NULL;
        NEW.revoked_by := NULL;
        NEW.proxy_email := upper(btrim(NEW.proxy_email));

        IF NEW.proxy_email !~ '^[^@[:space:]]+@[^@[:space:]]+$' THEN
            RAISE EXCEPTION 'A proxy is named by an email address' USING ERRCODE = '23514';
        END IF;

        IF NEW.expires_at IS NULL OR NEW.expires_at <= now() OR NEW.expires_at > now() + interval '1 year' THEN
            RAISE EXCEPTION 'A proxy grant expires in the future, within a year' USING ERRCODE = '23514';
        END IF;

        IF ehr.is_clinician() THEN
            NEW.granted_by_kind := 'staff';
            IF NEW.include_sensitive THEN
                RAISE EXCEPTION 'Only the patient allows a proxy to see sensitive items' USING ERRCODE = '42501';
            END IF;
        ELSE
            NEW.granted_by_kind := 'patient';
            IF NEW.proxy_email = ehr.current_account_email() THEN
                RAISE EXCEPTION 'A patient is not their own proxy' USING ERRCODE = '23514';
            END IF;

            IF (SELECT count(*) FROM ehr.proxy_grants g WHERE g.patient_id = NEW.patient_id AND g.revoked_at IS NULL) >= 20 THEN
                RAISE EXCEPTION 'A patient holds at most 20 proxy grants' USING ERRCODE = '54000';
            END IF;
        END IF;

        RETURN NEW;
    END IF;

    IF OLD.revoked_at IS NOT NULL THEN
        RAISE EXCEPTION 'A revoked proxy grant never changes' USING ERRCODE = '23514';
    END IF;

    IF NEW.include_sensitive IS DISTINCT FROM OLD.include_sensitive
       AND (ehr.own_patient_id() IS DISTINCT FROM OLD.patient_id OR ehr.acting_grant_id() IS NOT NULL) THEN
        RAISE EXCEPTION 'Only the patient allows a proxy to see sensitive items' USING ERRCODE = '42501';
    END IF;

    IF NEW.revoked_at IS NOT NULL THEN
        NEW.revoked_at := clock_timestamp();
        NEW.revoked_by := ehr.current_user_id();
    ELSE
        NEW.revoked_by := NULL;
    END IF;

    RETURN NEW;
END;
$$;

CREATE OR REPLACE TRIGGER proxy_grants_guard
    BEFORE INSERT OR UPDATE ON ehr.proxy_grants
    FOR EACH ROW EXECUTE FUNCTION ehr.guard_proxy_grant();
