-- Sync's change log. Every insert or update of a synced table adds a row to ehr.change_log, stamped with the
-- writing transaction's ID by the column's default, pg_current_xact_id(). A sync pull hands out only the rows of
-- transactions that have definitely finished (ChangeLog in Ehr.Data), so no change is ever skipped.
-- Applied as ehr_owner after migrations, on every run of infra/migrate; safe to re-run.
--
-- The function runs as its owner, ehr_owner (SECURITY DEFINER), so the runtime roles need no privilege on the log:
-- this trigger is the only way rows get there. Its search_path is pinned, so a caller can't put their own objects
-- in front of the ones it uses, and nobody but its owner may call it directly.

CREATE OR REPLACE FUNCTION ehr.log_change() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    INSERT INTO ehr.change_log (table_name, row_id) VALUES (TG_TABLE_NAME, NEW.id);
    RETURN NULL;
END
$$;

REVOKE ALL ON FUNCTION ehr.log_change() FROM PUBLIC;

-- Quick text, the walking skeleton's synced table. No delete trigger: phrases can't be deleted.
CREATE OR REPLACE TRIGGER quick_texts_change_log
    AFTER INSERT OR UPDATE ON ehr.quick_texts
    FOR EACH ROW EXECUTE FUNCTION ehr.log_change();
