-- The staff inbox (the expansion plan's step 3). Applied as ehr_owner after migrations, on every run of infra/migrate;
-- safe to re-run. The grants are in 10-grants.sql and the row-level security policies in 30-rls.sql: an item is read
-- and marked done by its recipient alone.
--
-- Marking an item done is the one change an item takes. The grant covers only done_at, row-level security only the
-- recipient's own items, and this trigger holds the update to exactly that, whoever makes it: an item that still needs
-- action becomes done, once, at the database's time whatever the client sent, and no other column moves. Nothing
-- reopens an item. Runs as the caller.
CREATE OR REPLACE FUNCTION ehr.complete_inbox_item() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF OLD.done_at IS NOT NULL OR NEW.done_at IS NULL THEN
        RAISE EXCEPTION 'An inbox item is marked done once' USING ERRCODE = '23514';
    END IF;

    -- An order's results are done by acknowledging them, which records who and when (75-results.sql): that marks
    -- the item done as the tables' owner. The apps, by the role they connect as, never tick one off directly.
    IF OLD.kind = 'result' AND OLD.source_table = 'orders' AND current_user IN ('ehr_app', 'ehr_read') THEN
        RAISE EXCEPTION 'Results are acknowledged on their own page, not marked done' USING ERRCODE = '23514';
    END IF;

    IF (to_jsonb(NEW) - 'done_at') IS DISTINCT FROM (to_jsonb(OLD) - 'done_at') THEN
        RAISE EXCEPTION 'An inbox item is never edited' USING ERRCODE = '42501';
    END IF;

    NEW.done_at := clock_timestamp();
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.complete_inbox_item() FROM PUBLIC;
CREATE OR REPLACE TRIGGER inbox_items_complete
    BEFORE UPDATE ON ehr.inbox_items
    FOR EACH ROW EXECUTE FUNCTION ehr.complete_inbox_item();
