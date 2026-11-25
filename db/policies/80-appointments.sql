-- Appointments (FR-17; the expansion plan's step 4). Applied as ehr_owner after migrations, on every run of
-- infra/migrate; safe to re-run. The grants are in 10-grants.sql and the row-level security policies in 30-rls.sql: a
-- clinician books and changes appointments only for a patient on their active panel. The function runs as the caller.
--
-- Before each insert or update, ehr.guard_appointment():
--   1. on insert, takes only a booked appointment, with a clinician who is one (a staff account with that role that
--      isn't disabled), and sets who booked it and when, whatever the client sent.
--   2. on update, lets an appointment make one of four moves and nothing else:
--        reschedule  booked -> booked, changing the start, the length or the reason;
--        cancel      booked -> cancelled, with a reason;
--        check in    booked -> fulfilled, naming the visit just created for the same patient;
--        no-show     booked -> noshow.
--      An appointment that is no longer booked never changes again, and the patient, the clinician, who booked it and
--      when never change at all.
--   0. refuses any transaction that isn't READ COMMITTED, and counts each change in the appointment's version.
--   3. refuses a booked appointment that overlaps another booked one of the same clinician. It locks the clinician's
--      staff row first, FOR NO KEY UPDATE, so one clinician's bookings are made one at a time: a second transaction
--      waits here until the first ends, and then sees its appointment. The error is exclusion_violation (23P01), which
--      the pages turn into "already booked then". The lock needs the UPDATE on staff_users that ehr_app has.
CREATE OR REPLACE FUNCTION ehr.guard_appointment() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    -- As for signing an order (60-orders.sql): under REPEATABLE READ or SERIALIZABLE the transaction that waited at
    -- the lock below would still look for an overlap in its old snapshot, miss the booking it waited for, and let
    -- both through. Page handlers run at READ COMMITTED.
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'An appointment is booked or changed in a READ COMMITTED transaction' USING ERRCODE = '0A000';
    END IF;

    IF TG_OP = 'INSERT' THEN
        IF NEW.status IS DISTINCT FROM 'booked' THEN
            RAISE EXCEPTION 'An appointment starts out booked' USING ERRCODE = '23514';
        END IF;

        NEW.booked_by_id := ehr.current_user_id();
        NEW.booked_at := clock_timestamp();
        NEW.version := 1;
    ELSE
        IF OLD.status IS DISTINCT FROM 'booked' THEN
            RAISE EXCEPTION 'An appointment that is no longer booked does not change' USING ERRCODE = '23514';
        END IF;

        IF (NEW.id, NEW.patient_id, NEW.clinician_id, NEW.booked_by_id, NEW.booked_at)
           IS DISTINCT FROM (OLD.id, OLD.patient_id, OLD.clinician_id, OLD.booked_by_id, OLD.booked_at) THEN
            RAISE EXCEPTION 'An appointment keeps its patient, its clinician and who booked it' USING ERRCODE = '42501';
        END IF;

        -- Each change is one more version, whatever the client sent: the app's updates name the version they began
        -- from, so one that began from an older version finds no row.
        NEW.version := OLD.version + 1;

        IF NEW.status <> 'booked'
           AND (NEW.starts_at, NEW.minutes, NEW.reason) IS DISTINCT FROM (OLD.starts_at, OLD.minutes, OLD.reason) THEN
            RAISE EXCEPTION 'An appointment is rescheduled only while it stays booked' USING ERRCODE = '23514';
        END IF;

        IF NEW.status = 'cancelled' AND coalesce(NEW.cancel_reason, '') !~ '[^[:space:]]' THEN
            RAISE EXCEPTION 'Cancelling an appointment needs a reason' USING ERRCODE = '23514';
        END IF;

        IF NEW.status = 'fulfilled' THEN
            PERFORM 1 FROM ehr.encounters e WHERE e.id = NEW.encounter_id AND e.patient_id = OLD.patient_id;
            IF NOT FOUND THEN
                RAISE EXCEPTION 'Checking in names the visit of the same patient' USING ERRCODE = '23514';
            END IF;
        END IF;
    END IF;

    IF NEW.status = 'booked' THEN
        PERFORM 1 FROM ehr.staff_users s
        -- A clinician account that isn't disabled. A disabled account is locked out for good; one locked for a few
        -- minutes after wrong passwords is still a clinician with a schedule.
        WHERE s.id = NEW.clinician_id AND s.roles @> ARRAY['Clinician'] AND (s.lockout_end IS NULL OR s.lockout_end < now() + interval '10 years')
        FOR NO KEY UPDATE;
        IF NOT FOUND THEN
            RAISE EXCEPTION 'An appointment is with a clinician' USING ERRCODE = '23514';
        END IF;

        IF EXISTS (
            SELECT 1 FROM ehr.appointments a
            WHERE a.clinician_id = NEW.clinician_id AND a.status = 'booked' AND a.id <> NEW.id
              -- No appointment is longer than 240 minutes, so none that starts earlier than this can reach it.
              AND a.starts_at > NEW.starts_at - interval '240 minutes'
              AND a.starts_at < NEW.starts_at + make_interval(mins => NEW.minutes)
              AND NEW.starts_at < a.starts_at + make_interval(mins => a.minutes)) THEN
            RAISE EXCEPTION 'The clinician already has an appointment booked then' USING ERRCODE = '23P01';
        END IF;
    END IF;

    RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION ehr.guard_appointment() FROM PUBLIC;
CREATE OR REPLACE TRIGGER appointments_guard
    BEFORE INSERT OR UPDATE ON ehr.appointments
    FOR EACH ROW EXECUTE FUNCTION ehr.guard_appointment();
