-- Enrollment codes and proxy invitations (the expansion plan's step 6c): the only ways the apps reach
-- ehr.patient_enrollments and ehr.proxy_invitations. The runtime roles have no privilege on either table
-- (10-grants.sql). Each function runs as its owner, sets its search_path, and is executable only by the roles named
-- here. Plain SQL, safe to run again.
--
-- Why functions: both tables are used by pages that run before anyone is signed in (registering, accepting an
-- invitation), so row-level security has no user to decide by, and a table grant would let an app session with nobody
-- signed in list every row: who was invited, which patients have codes waiting. A function answers for the one code
-- it is given and nothing else.
--
-- A code is never stored. The tables hold a hash, and a row is found by the hash of what was typed, through the
-- unique index: so there is nothing to compare afterwards (the stored hash is the computed one), and the index's
-- comparison is of hashes, which someone guessing can't steer. An invitation's code is 160 random bits, hashed with
-- SHA-256. An enrollment code is short enough to type (12 characters, about 60 bits), so its hash is keyed
-- (HMAC-SHA-256 with a key only the app has): a copy of the table can't be searched offline for codes.

-- Issues an enrollment code for a patient: by a clinician, as themselves, for a patient that exists. Fourteen days.
CREATE OR REPLACE FUNCTION ehr.issue_patient_enrollment(enrollment_id uuid, patient uuid, hash text) RETURNS void
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF NOT coalesce(ehr.is_clinician(), false) THEN
        RAISE EXCEPTION 'Only a clinician issues an enrollment code' USING ERRCODE = '42501';
    END IF;

    IF hash IS NULL OR hash !~ '^[0-9a-f]{64}$' THEN
        RAISE EXCEPTION 'An enrollment code is stored as its hash' USING ERRCODE = '23514';
    END IF;

    INSERT INTO ehr.patient_enrollments (id, patient_id, code_hash, issued_by, issued_at, expires_at)
    VALUES (enrollment_id, patient, hash, ehr.current_user_id(), clock_timestamp(), clock_timestamp() + interval '14 days');
END
$$;
REVOKE ALL ON FUNCTION ehr.issue_patient_enrollment(uuid, uuid, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ehr.issue_patient_enrollment(uuid, uuid, text) TO ehr_app;

-- The one enrollment a code opens, while it is unused and unexpired: its ID and its patient. `legacy` is the hash
-- codes had before they were keyed; such rows are at most fourteen days old and then none is left. A function of
-- its own found it without using it up, and the apps never called it: it goes, from a database that has it too.
DROP FUNCTION IF EXISTS ehr.find_patient_enrollment(text, text);

-- Uses that enrollment up: it is marked redeemed, once, and answers with its ID and its patient. Nothing for a code that is
-- unknown, used or expired; of two calls with the same code, the second waits on the row and then gets nothing.
CREATE OR REPLACE FUNCTION ehr.redeem_patient_enrollment(hash text, legacy text) RETURNS TABLE (id uuid, patient_id uuid)
    LANGUAGE sql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
    UPDATE ehr.patient_enrollments e SET redeemed_at = clock_timestamp()
    WHERE e.id = (SELECT f.id FROM ehr.patient_enrollments f
                  WHERE f.code_hash IN (hash, legacy) AND f.redeemed_at IS NULL AND f.expires_at > now() LIMIT 1)
      AND e.redeemed_at IS NULL
    RETURNING e.id, e.patient_id
$$;
REVOKE ALL ON FUNCTION ehr.redeem_patient_enrollment(text, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ehr.redeem_patient_enrollment(text, text) TO ehr_app;

-- Records an invitation for a proxy grant: only for a grant the signed-in user gave, in force, and to the address
-- that grant names, taken from the grant and never from the caller. Seven days.
CREATE OR REPLACE FUNCTION ehr.issue_proxy_invitation(invitation_id uuid, proxy_grant uuid, hash text) RETURNS void
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    address text;
BEGIN
    IF hash IS NULL OR hash !~ '^[0-9a-f]{64}$' THEN
        RAISE EXCEPTION 'An invitation is stored as the hash of its code' USING ERRCODE = '23514';
    END IF;

    SELECT lower(g.proxy_email) INTO address FROM ehr.proxy_grants g
    WHERE g.id = proxy_grant AND g.granted_by = ehr.current_user_id() AND g.revoked_at IS NULL AND g.expires_at > now();
    IF address IS NULL THEN
        RAISE EXCEPTION 'An invitation is sent for access the signed-in user gave' USING ERRCODE = '42501';
    END IF;

    INSERT INTO ehr.proxy_invitations (id, grant_id, email, code_hash, issued_at, expires_at)
    VALUES (invitation_id, proxy_grant, address, hash, clock_timestamp(), clock_timestamp() + interval '7 days');
END
$$;
REVOKE ALL ON FUNCTION ehr.issue_proxy_invitation(uuid, uuid, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ehr.issue_proxy_invitation(uuid, uuid, text) TO ehr_app;

-- The one invitation a code opens, while it is unused and unexpired: its ID and the address it was sent to.
CREATE OR REPLACE FUNCTION ehr.find_proxy_invitation(hash text) RETURNS TABLE (id uuid, email text)
    LANGUAGE sql
    STABLE
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
    SELECT i.id, i.email::text FROM ehr.proxy_invitations i
    WHERE i.code_hash = hash AND i.redeemed_at IS NULL AND i.expires_at > now()
$$;
REVOKE ALL ON FUNCTION ehr.find_proxy_invitation(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ehr.find_proxy_invitation(text) TO ehr_app, ehr_read;

-- Uses that invitation up, once, and answers as find does; nothing otherwise.
CREATE OR REPLACE FUNCTION ehr.redeem_proxy_invitation(hash text) RETURNS TABLE (id uuid, email text)
    LANGUAGE sql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
    UPDATE ehr.proxy_invitations i SET redeemed_at = clock_timestamp()
    WHERE i.code_hash = hash AND i.redeemed_at IS NULL AND i.expires_at > now()
    RETURNING i.id, i.email::text
$$;
REVOKE ALL ON FUNCTION ehr.redeem_proxy_invitation(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ehr.redeem_proxy_invitation(text) TO ehr_app;
