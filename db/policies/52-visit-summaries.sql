-- Visit summaries (the expansion plan's step 13a): what a clinician writes for the patient from a signed note and
-- releases to the patient portal. What the database itself holds them to, whatever the app does. Who may write,
-- withdraw, mark and read one is row-level security's (30-rls.sql); the grants are in 10-grants.sql. Plain SQL, safe to
-- run again. Tahseen's decisions of 21 November 2026 shape the sensitivity and the withdrawal: a summary is sensitive
-- when its note or any of the note's addenda is (inherited), or when a clinician marks it; a withdrawn one leaves the
-- portal a dated placeholder; its releaser is told when someone else withdraws it; and the patient is told when it had
-- been viewed in the portal before it was withdrawn.

-- What in a summary isn't fit for the patient: the one statement of the rule, which the trigger below enforces and the
-- page asks to explain a refusal (VisitSummaryChecks in Ehr.Data). One row for each thing found: the part of the
-- summary it is in (1 What we talked about, 2 What we found, 3 What happens next), what was found, and its kind.
--   identifier      a UUID, with its hyphens or as 32 hex digits
--   record-number   the patient's own MRN, as a whole word
--   diagnosis-code  an ICD-10-CM code of the reference data, written with its dot or without ("E11.9", "E119", "I10");
--                   a category the reference data doesn't hold as a code ("E11", "N95") is not one
--   medication-code an RxNorm code: 5 to 7 digits standing alone, not part of a grouped or decimal number. Shorter
--                   ones aren't checked: 1998 is captopril's code and a year
--   lab-code        a LOINC code the clinic records or orders: digits, a hyphen and its check digit ("4548-4")
--   substance-code  a UNII of the reference data: 10 letters and digits
--   order-code      the clinic's own order codes ("SYN-LAB-001")
--   note-section    the note's whole History, Assessment or Plan, when it is 40 characters or more, copied in
--                   (compared with case and runs of white space ignored)
-- A word counts as one when it stands alone: nothing but a letter or digit may touch it (and for a number, no
-- separator joining it to more digits), and case doesn't matter. Each pattern must match a real code, so ordinary
-- words, doses and dates pass: "B12", "A1c", "500 mg", "50,000 units", "2026-11-21", "1-2 tablets". Runs as the
-- caller, who must be able to read the patient and the note: a clinician.
CREATE OR REPLACE FUNCTION ehr.visit_summary_problems(summary_patient uuid, summary_note uuid, parts text[])
    RETURNS TABLE (part integer, found text, kind text)
    LANGUAGE sql
    STABLE
    SET search_path = pg_catalog, pg_temp
AS $$
    WITH part_text AS (
        SELECT p.ordinality::integer AS part, coalesce(p.body, '') AS body
        FROM unnest(parts) WITH ORDINALITY AS p(body, ordinality)
    ),
    words AS (
        SELECT t.part, m[1] AS word, 'identifier' AS shape
        FROM part_text t, regexp_matches(t.body, '(?<![[:alnum:]])([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-f]{32})(?![[:alnum:]])', 'gi') AS m
        UNION ALL
        SELECT t.part, m[1], 'diagnosis'
        FROM part_text t, regexp_matches(t.body, '(?<![[:alnum:].])([a-z][0-9][0-9a-z](?:\.[0-9a-z]{1,4}|[0-9a-z]{1,4})?)(?![[:alnum:]]|\.[[:alnum:]])', 'gi') AS m
        UNION ALL
        SELECT t.part, m[1], 'medication'
        FROM part_text t, regexp_matches(t.body, '(?<![[:alnum:].,/-])([0-9]{5,7})(?![[:alnum:]]|[.,/-][0-9])', 'g') AS m
        UNION ALL
        SELECT t.part, m[1], 'lab'
        FROM part_text t, regexp_matches(t.body, '(?<![[:alnum:].,/-])([0-9]{1,7}-[0-9])(?![[:alnum:]]|[.,/-][0-9])', 'g') AS m
        UNION ALL
        SELECT t.part, m[1], 'substance'
        FROM part_text t, regexp_matches(t.body, '(?<![[:alnum:]])([0-9a-z]{10})(?![[:alnum:]])', 'gi') AS m
        WHERE m[1] ~ '[0-9]'
        UNION ALL
        SELECT t.part, m[1], 'order'
        FROM part_text t, regexp_matches(t.body, '(?<![[:alnum:]-])(syn-[0-9a-z-]*[0-9a-z])(?![[:alnum:]-])', 'gi') AS m
    ),
    note AS (
        SELECT n.history, n.assessment, n.plan FROM ehr.clinical_notes n WHERE n.id = summary_note AND n.patient_id = summary_patient
    ),
    mrn AS (
        SELECT p.mrn::text AS mrn FROM ehr.patients p WHERE p.id = summary_patient
    )
    SELECT w.part, w.word, 'identifier'
    FROM words w WHERE w.shape = 'identifier'
    UNION
    SELECT w.part, w.word, 'diagnosis-code'
    FROM words w
    WHERE w.shape = 'diagnosis' AND EXISTS (
        SELECT 1 FROM ehr.clinical_codes c
        WHERE c.kind = 'condition' AND c.system = 'icd-10-cm'
          AND c.code = CASE WHEN w.word LIKE '%.%' OR length(w.word) = 3 THEN upper(w.word)
                            ELSE upper(left(w.word, 3) || '.' || substr(w.word, 4)) END)
    UNION
    SELECT w.part, w.word, 'medication-code'
    FROM words w
    WHERE w.shape = 'medication' AND EXISTS (
        SELECT 1 FROM ehr.clinical_codes c WHERE c.kind IN ('medication', 'allergen') AND c.system = 'rxnorm' AND c.code = w.word)
    UNION
    SELECT w.part, w.word, 'lab-code'
    FROM words w
    WHERE w.shape = 'lab' AND (
        EXISTS (SELECT 1 FROM ehr.observation_codes c WHERE c.code = w.word)
        OR EXISTS (SELECT 1 FROM ehr.order_catalog_items i WHERE i.code = w.word)
        OR EXISTS (SELECT 1 FROM ehr.order_catalog_items i WHERE i.standard_system = 'loinc' AND i.standard_code = w.word))
    UNION
    SELECT w.part, w.word, 'substance-code'
    FROM words w
    WHERE w.shape = 'substance' AND EXISTS (
        SELECT 1 FROM ehr.clinical_codes c WHERE c.kind = 'allergen' AND c.system = 'unii' AND c.code = upper(w.word))
    UNION
    SELECT w.part, w.word, 'order-code'
    FROM words w
    WHERE w.shape = 'order' AND EXISTS (SELECT 1 FROM ehr.order_catalog_items i WHERE i.code = upper(w.word))
    UNION
    SELECT t.part, substring(t.body FROM '(?i)(?<![[:alnum:]])' || regexp_replace(m.mrn, '([^[:alnum:]])', '\\\1', 'g') || '(?![[:alnum:]])'), 'record-number'
    FROM part_text t, mrn m
    WHERE length(m.mrn) > 0 AND t.body ~* ('(?<![[:alnum:]])' || regexp_replace(m.mrn, '([^[:alnum:]])', '\\\1', 'g') || '(?![[:alnum:]])')
    UNION
    SELECT t.part, s.name, 'note-section'
    FROM part_text t, note n,
        LATERAL (VALUES ('History', n.history), ('Assessment', n.assessment), ('Plan', n.plan)) AS s(name, body),
        LATERAL (SELECT lower(regexp_replace(btrim(s.body), '[[:space:]]+', ' ', 'g')) AS section,
                        lower(regexp_replace(btrim(t.body), '[[:space:]]+', ' ', 'g')) AS summary) AS plain
    WHERE length(plain.section) >= 40 AND position(plain.section IN plain.summary) > 0
    ORDER BY 1, 3, 2
$$;
REVOKE ALL ON FUNCTION ehr.visit_summary_problems(uuid, uuid, text[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ehr.visit_summary_problems(uuid, uuid, text[]) TO ehr_app;

-- Whether a note, or any addendum of it, is sensitive: what a summary of it inherits its mark from. Runs as the caller,
-- who must be able to read them: a clinician.
CREATE OR REPLACE FUNCTION ehr.summary_sources_sensitive(source_note uuid, source_patient uuid) RETURNS boolean
    LANGUAGE sql
    STABLE
    SET search_path = pg_catalog, pg_temp
AS $$
    SELECT EXISTS (SELECT 1 FROM ehr.clinical_notes n WHERE n.id = source_note AND n.patient_id = source_patient AND n.sensitive)
        OR EXISTS (SELECT 1 FROM ehr.note_addenda a WHERE a.note_id = source_note AND a.patient_id = source_patient AND a.sensitive)
$$;
REVOKE ALL ON FUNCTION ehr.summary_sources_sensitive(uuid, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ehr.summary_sources_sensitive(uuid, uuid) TO ehr_app;

-- Before a summary is inserted or updated, whoever makes the change, the tables' owner included. Runs as the caller.
--   Insert, as row-level security has already required a clinician with the patient on their panel, as themselves:
--     in a READ COMMITTED transaction (below); one at a time with the note's or an addendum's sensitivity changing and
--     an addendum being signed, under the note's lock (the one ehr.change_note_sensitivity, the addenda's sensitivity
--     and their signing take), so what is read next is the note as it will stand; the note must be this patient's and
--     readable by the caller; who released it and when are the database's, and so is the note's date; it starts
--     standing; nothing ehr.visit_summary_problems finds may be in it. Sensitive if the note or any of its addenda is
--     (inherited), or if it was sent as sensitive (its own mark, by the person releasing it), as an addendum is signed.
--   Update: one of two changes, and nothing else.
--     Withdrawn, once, with a reason of up to 500 characters; who and when are the database's.
--     Marked sensitive, or unmarked, with a reason of up to 500 characters, under the note's lock, as an addendum is
--     (50-clinical-notes.sql): marked while the note or an addendum is sensitive, the mark is inherited; otherwise it
--     is the marker's own. Never unmarked while the note or an addendum is sensitive; a mark of its own only by the
--     person who made it (whoever released it, if nobody was recorded) or the note's signer; an inherited mark goes
--     with the last of its sources' (ehr.follow_summary_sources, below), whoever unmarked that.
-- READ COMMITTED, as for filing a result (75-results.sql): under a snapshot taken before the lock was granted, the
-- sensitivity read here could be the one from before a change that has since been committed.
CREATE OR REPLACE FUNCTION ehr.guard_visit_summary() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    note_signed timestamptz;
    note_signer uuid;
    sources boolean;
    standing constant text[] := ARRAY['withdrawn_at', 'withdrawn_by', 'withdrawn_reason'];
    marks constant text[] := ARRAY['sensitive', 'sensitive_inherited', 'sensitive_marked_by', 'sensitive_reason', 'sensitive_changed_at'];
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF current_setting('transaction_isolation') <> 'read committed' THEN
            RAISE EXCEPTION 'A visit summary is released in a READ COMMITTED transaction' USING ERRCODE = '0A000';
        END IF;

        PERFORM pg_advisory_xact_lock(hashtextextended('ehr.clinical_notes:' || NEW.note_id::text, 0));
        SELECT n.signed_at INTO note_signed FROM ehr.clinical_notes n WHERE n.id = NEW.note_id AND n.patient_id = NEW.patient_id;
        IF NOT FOUND THEN
            RAISE EXCEPTION 'The signed note is unavailable' USING ERRCODE = '23514';
        END IF;
        sources := ehr.summary_sources_sensitive(NEW.note_id, NEW.patient_id);

        NEW.released_by := ehr.current_user_id();
        NEW.released_at := clock_timestamp();
        NEW.note_signed_at := note_signed;
        NEW.sensitive_inherited := sources AND NOT coalesce(NEW.sensitive, false);
        NEW.sensitive := coalesce(NEW.sensitive, false) OR sources;
        NEW.sensitive_marked_by := CASE WHEN NEW.sensitive AND NOT NEW.sensitive_inherited THEN ehr.current_user_id() END;
        NEW.sensitive_reason := NULL;
        NEW.sensitive_changed_at := NULL;
        NEW.withdrawn_at := NULL;
        NEW.withdrawn_by := NULL;
        NEW.withdrawn_reason := NULL;
        IF EXISTS (SELECT 1 FROM ehr.visit_summary_problems(NEW.patient_id, NEW.note_id, ARRAY[NEW.talked_about, NEW.found, NEW.next_steps])) THEN
            RAISE EXCEPTION 'A visit summary has no codes, record numbers or identifiers, and copies no whole section of its note'
                USING ERRCODE = '23514', CONSTRAINT = 'visit_summaries_plain';
        END IF;
        RETURN NEW;
    END IF;

    IF (to_jsonb(NEW) - standing) IS NOT DISTINCT FROM (to_jsonb(OLD) - standing)
       AND NEW.withdrawn_reason IS DISTINCT FROM OLD.withdrawn_reason THEN
        IF OLD.withdrawn_at IS NOT NULL THEN
            RAISE EXCEPTION 'A visit summary is withdrawn once' USING ERRCODE = '23514';
        END IF;
        NEW.withdrawn_reason := btrim(NEW.withdrawn_reason);
        IF NEW.withdrawn_reason IS NULL OR NEW.withdrawn_reason !~ '[^[:space:]]' OR char_length(NEW.withdrawn_reason) > 500 THEN
            RAISE EXCEPTION 'Say why the summary is withdrawn, in up to 500 characters' USING ERRCODE = '23514';
        END IF;
        IF ehr.current_user_id() IS NULL THEN
            RAISE EXCEPTION 'A visit summary is withdrawn by someone' USING ERRCODE = '42501';
        END IF;
        NEW.withdrawn_by := ehr.current_user_id();
        NEW.withdrawn_at := clock_timestamp();
        RETURN NEW;
    END IF;

    IF (to_jsonb(NEW) - marks) IS DISTINCT FROM (to_jsonb(OLD) - marks) THEN
        RAISE EXCEPTION 'A released visit summary never changes: a wrong one is withdrawn' USING ERRCODE = '23514';
    END IF;
    IF NEW.sensitive IS NOT DISTINCT FROM OLD.sensitive THEN
        RAISE EXCEPTION 'A visit summary changes only by being withdrawn, or marked sensitive or unmarked' USING ERRCODE = '23514';
    END IF;
    NEW.sensitive_reason := btrim(NEW.sensitive_reason);
    IF NEW.sensitive_reason IS NULL OR NEW.sensitive_reason !~ '[^[:space:]]' OR char_length(NEW.sensitive_reason) > 500 THEN
        RAISE EXCEPTION 'Say why, in up to 500 characters' USING ERRCODE = '23514';
    END IF;

    -- One at a time with the note and its addenda being marked or unmarked, so what is read next is as it will stand.
    PERFORM pg_advisory_xact_lock(hashtextextended('ehr.clinical_notes:' || OLD.note_id::text, 0));
    SELECT n.signer_id INTO note_signer FROM ehr.clinical_notes n WHERE n.id = OLD.note_id AND n.patient_id = OLD.patient_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'The signed note is unavailable' USING ERRCODE = '23514';
    END IF;
    sources := ehr.summary_sources_sensitive(OLD.note_id, OLD.patient_id);

    IF NEW.sensitive THEN
        IF ehr.current_user_id() IS NULL THEN
            RAISE EXCEPTION 'A visit summary is marked sensitive by someone' USING ERRCODE = '42501';
        END IF;
        NEW.sensitive_inherited := sources;
        NEW.sensitive_marked_by := CASE WHEN sources THEN NULL ELSE ehr.current_user_id() END;
    ELSE
        IF sources THEN
            RAISE EXCEPTION 'A summary of a sensitive note or addendum is sensitive' USING ERRCODE = '23514';
        END IF;
        IF NOT OLD.sensitive_inherited
           AND (ehr.current_user_id() IS NULL
                OR (ehr.current_user_id() IS DISTINCT FROM coalesce(OLD.sensitive_marked_by, OLD.released_by) AND ehr.current_user_id() IS DISTINCT FROM note_signer)) THEN
            RAISE EXCEPTION 'Only the person who marked a summary sensitive, or the note''s signer, unmarks it'
                USING ERRCODE = '42501', CONSTRAINT = 'visit_summaries_unmark';
        END IF;
        NEW.sensitive_inherited := false;
        NEW.sensitive_marked_by := NULL;
    END IF;
    NEW.sensitive_changed_at := clock_timestamp();
    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.guard_visit_summary() FROM PUBLIC;
CREATE OR REPLACE TRIGGER visit_summaries_guard
    BEFORE INSERT OR UPDATE ON ehr.visit_summaries
    FOR EACH ROW EXECUTE FUNCTION ehr.guard_visit_summary();

-- A released summary is never deleted, by anyone: a wrong one is withdrawn, and stays in the record.
CREATE OR REPLACE FUNCTION ehr.reject_visit_summary_delete() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    RAISE EXCEPTION 'A visit summary is never deleted: a wrong one is withdrawn' USING ERRCODE = '23514';
END
$$;
REVOKE ALL ON FUNCTION ehr.reject_visit_summary_delete() FROM PUBLIC;
CREATE OR REPLACE TRIGGER visit_summaries_immutable
    BEFORE DELETE ON ehr.visit_summaries
    FOR EACH ROW EXECUTE FUNCTION ehr.reject_visit_summary_delete();

-- A note's summaries follow its sensitivity and its addenda's (Tahseen's decision of 21 November 2026), as an addendum
-- follows its note's (50-clinical-notes.sql). When the note or one of its addenda becomes sensitive (marked, or an
-- addendum signed sensitive), each of its summaries not already sensitive is marked, recorded as inherited, with the
-- reason given; when one is unmarked and none of them is sensitive any more, the inherited marks are cleared, with
-- that reason, and only those: a summary marked on its own stays marked until it is unmarked itself. Withdrawn
-- summaries follow too: their placeholders in the portal depend on it. As the caller, through the summaries' own rule
-- above, which decides that a mark is inherited, and the panel policy (30-rls.sql); the person who changes a note or an
-- addendum is a clinician with the patient on their panel already.
-- When it runs: AFTER triggers on clinical_notes and note_addenda, so at the end of the statement that changed the note
-- or the addendum, under the note's lock, which ehr.change_note_sensitivity, ehr.change_addendum_sensitivity and
-- ehr.sign_note_addendum take before the row changes and the summaries' rule takes too. It recomputes from the note and
-- its addenda as they stand, so it relies on no order among the other AFTER triggers, which PostgreSQL fires in the order
-- of their names: on the notes clinical_notes_audit, clinical_notes_clear_inherited, clinical_notes_follow_summaries,
-- clinical_notes_log_sensitivity, clinical_notes_mark_addenda (whose changes to the addenda run this again, for each);
-- on the addenda note_addenda_audit, note_addenda_deliver, note_addenda_follow_summaries or
-- note_addenda_signed_follow_summaries. A summary released while a change waits for the lock is found by a statement
-- that starts after the lock was granted, which READ COMMITTED alone makes sure of: so it is refused under any other level
-- (the apps use READ COMMITTED).
CREATE OR REPLACE FUNCTION ehr.follow_summary_sources() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    source_note uuid := CASE WHEN TG_TABLE_NAME = 'clinical_notes' THEN (to_jsonb(NEW) ->> 'id')::uuid ELSE (to_jsonb(NEW) ->> 'note_id')::uuid END;
    why text := coalesce(NEW.sensitive_reason, 'An addendum to the note was signed as sensitive.');
    sources boolean;
BEGIN
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'A note''s or an addendum''s sensitivity changes in a READ COMMITTED transaction' USING ERRCODE = '0A000';
    END IF;

    sources := ehr.summary_sources_sensitive(source_note, NEW.patient_id);
    IF sources THEN
        UPDATE ehr.visit_summaries s SET sensitive = true, sensitive_reason = why
        WHERE s.note_id = source_note AND s.patient_id = NEW.patient_id AND NOT s.sensitive;
    ELSE
        UPDATE ehr.visit_summaries s SET sensitive = false, sensitive_reason = why
        WHERE s.note_id = source_note AND s.patient_id = NEW.patient_id AND s.sensitive AND s.sensitive_inherited;
    END IF;
    -- All of them, or the change doesn't happen: an update the policies hid from this caller would leave one behind.
    IF EXISTS (SELECT 1 FROM ehr.visit_summaries s
               WHERE s.note_id = source_note AND (sources AND NOT s.sensitive OR NOT sources AND s.sensitive_inherited)) THEN
        RAISE EXCEPTION 'The note''s visit summaries could not follow its sensitivity' USING ERRCODE = '42501';
    END IF;
    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.follow_summary_sources() FROM PUBLIC;
CREATE OR REPLACE TRIGGER clinical_notes_follow_summaries
    AFTER UPDATE ON ehr.clinical_notes
    FOR EACH ROW WHEN (NEW.sensitive IS DISTINCT FROM OLD.sensitive)
    EXECUTE FUNCTION ehr.follow_summary_sources();
CREATE OR REPLACE TRIGGER note_addenda_follow_summaries
    AFTER UPDATE ON ehr.note_addenda
    FOR EACH ROW WHEN (NEW.sensitive IS DISTINCT FROM OLD.sensitive)
    EXECUTE FUNCTION ehr.follow_summary_sources();
CREATE OR REPLACE TRIGGER note_addenda_signed_follow_summaries
    AFTER INSERT ON ehr.note_addenda
    FOR EACH ROW WHEN (NEW.sensitive)
    EXECUTE FUNCTION ehr.follow_summary_sources();

-- When a summary is withdrawn by someone other than the clinician who released it, the releaser is told, as a note's
-- signer is told of another clinician's addendum (step 10b, ehr.deliver_addendum): an inbox item, "A visit summary you
-- released was withdrawn", with the patient and a link to the note, and no text of the summary or the reason. Only while
-- the releaser is a clinician who can sign in; otherwise nobody is told, and the withdrawal is on the note for whoever
-- opens it. Runs as its owner, which alone may add inbox items (10-grants.sql); it reads nothing but the staff account.
CREATE OR REPLACE FUNCTION ehr.deliver_summary_withdrawn() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    INSERT INTO ehr.inbox_items (id, recipient_id, kind, patient_id, source_table, source_id, summary, created_at)
    SELECT uuidv7(), s.id, 'summary-withdrawn', NEW.patient_id, 'clinical_notes', NEW.note_id, 'A visit summary you released was withdrawn', clock_timestamp()
    FROM ehr.staff_users s
    WHERE s.id = NEW.released_by AND NEW.released_by IS DISTINCT FROM NEW.withdrawn_by
      AND s.roles @> ARRAY['Clinician'] AND (s.lockout_end IS NULL OR s.lockout_end <= now());
    RETURN NULL;
END
$$;
REVOKE ALL ON FUNCTION ehr.deliver_summary_withdrawn() FROM PUBLIC;
CREATE OR REPLACE TRIGGER visit_summaries_deliver_withdrawn
    AFTER UPDATE ON ehr.visit_summaries
    FOR EACH ROW WHEN (OLD.withdrawn_at IS NULL AND NEW.withdrawn_at IS NOT NULL)
    EXECUTE FUNCTION ehr.deliver_summary_withdrawn();

-- The portal's placeholders for withdrawn summaries (Tahseen's decision of 21 November 2026): for the record the
-- session is looking at, the date of each withdrawn summary and whether it is sensitive, and nothing else of it, under
-- the rule a standing summary is read by (30-rls.sql, visit_summaries_own_select): the patient's own, and a proxy's only
-- while their access is in force, a sensitive one only when the patient allows them sensitive items. Row-level
-- security can't keep a row's text from a session that may read the row, so the portal is given no withdrawn row at all,
-- only this. Runs as its owner, reading the table through the owner's policy (30-rls.sql); its search_path is pinned,
-- and only the runtime roles may call it. Anyone who isn't a portal session looking at a record gets nothing.
CREATE OR REPLACE FUNCTION ehr.withdrawn_visit_summaries()
    RETURNS TABLE (note_signed_at timestamptz, sensitive boolean)
    LANGUAGE sql
    STABLE
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
    SELECT s.note_signed_at, s.sensitive
    FROM ehr.visit_summaries s
    WHERE s.patient_id = (SELECT ehr.current_patient_id()) AND s.withdrawn_at IS NOT NULL
      AND (NOT s.sensitive OR (SELECT ehr.portal_sees_sensitive()))
    ORDER BY s.note_signed_at DESC, s.id
    LIMIT 100
$$;
REVOKE ALL ON FUNCTION ehr.withdrawn_visit_summaries() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ehr.withdrawn_visit_summaries() TO ehr_app, ehr_read;

-- Whether a summary the caller has just withdrawn had been viewed in the portal (Tahseen's decision of 21 November
-- 2026: if so, the patient is emailed). Viewed means the portal recorded a read of that summary, which it does for each
-- summary its page shows (PhiReader.VisitSummariesAsync), by a portal account: the patient's own or a proxy's. Its own
-- read of the summary's row is the only one there is, so no comparison of clocks is needed: a withdrawn summary is shown
-- to nobody in the portal. A view made off-site reaches this log when the cloud app ships it, every couple of seconds,
-- so one not yet shipped at the moment of withdrawal is missed. The caller can't read the audit log: this answers only
-- for a summary withdrawn in the caller's own transaction, by the caller, a clinician. Runs as its owner, reading the
-- summary and the audit log through the owner's policies (30-rls.sql); its search_path is pinned, and only the app's
-- role may call it.
CREATE OR REPLACE FUNCTION ehr.withdrawn_summary_was_viewed(summary uuid) RETURNS boolean
    LANGUAGE sql
    STABLE
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
    SELECT EXISTS (
        SELECT 1
        FROM ehr.visit_summaries s
        JOIN ehr.audit_events e ON e.patient_id = s.patient_id AND e.table_name = 'visit_summaries' AND e.row_id = s.id AND e.action = 'read'
        JOIN ehr.patient_users u ON u.id = e.actor_id
        WHERE s.id = summary AND s.withdrawn_at >= now() AND s.withdrawn_by = (SELECT ehr.current_user_id()) AND (SELECT ehr.is_clinician()))
$$;
REVOKE ALL ON FUNCTION ehr.withdrawn_summary_was_viewed(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ehr.withdrawn_summary_was_viewed(uuid) TO ehr_app;
