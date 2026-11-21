using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// FR-19 (the expansion plan's step 6): what the database lets a proxy do, as the runtime roles, in SQL of its own
// (db/policies/10-grants.sql, 30-rls.sql, 40-audit.sql and 95-proxy-access.sql). The app only names the grant a
// session acts under; here the database is asked directly, with grants that are someone else's, revoked, expired or
// made up. A proxy sees the patient's record only under a grant that is theirs and in force, never sensitive items or
// messages unless the grant says so, and never their own and the patient's at once; only the patient and their care
// team give and revoke grants; and everything a proxy does is recorded under the proxy's account and the patient.
public class ProxyAccessSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    const string Grant = """
        INSERT INTO ehr.proxy_grants (id, patient_id, proxy_email, scope, include_sensitive, expires_at, granted_by, granted_at, granted_by_kind)
        VALUES (@id, @patient, @email, @scope, @sensitive, now() + @days * interval '1 day', gen_random_uuid(), '2000-01-01', 'staff')
        """;

    const string Seen = "SELECT DISTINCT patient_id FROM ehr.observations";

    sealed record Person(Guid Patient, Guid Account, string Email);

    async Task<(Guid Clinician, Person Patient, Person Proxy)> PeopleAsync()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await PersonAsync(clinician);
        await Accounts.AssignAsync(database, clinician, patient.Patient);
        return (clinician, patient, await PersonAsync(clinician));
    }

    // A patient with a confirmed portal account and one released result.
    async Task<Person> PersonAsync(Guid clinician)
    {
        var patient = await Accounts.PatientAsync(database, clinician);
        var account = await Accounts.PatientAccountAsync(database, patient);
        await Accounts.ChartRowAsync(database, "observations", patient);
        return new Person(patient, account, $"{account:N}@example.com");
    }

    async Task<Guid> GrantAsync(Guid? by, Person patient, Person proxy, string scope = "record", bool sensitive = false, int days = 30, string? connection = null)
    {
        var id = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(connection ?? App, by, Grant, ("id", id), ("patient", patient.Patient), ("email", proxy.Email), ("scope", scope),
            ("sensitive", sensitive), ("days", days));
        return id;
    }

    Task<List<string>> AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.QueryAsync(database.SuperuserConnectionString, null, sql, parameters);

    async Task<List<string>> ActingAsync(string connection, Guid account, Guid grant, string sql, params (string Name, object Value)[] parameters)
    {
        using (Accounts.ActingUnder(grant))
        {
            return await Accounts.QueryAsync(connection, account, sql, parameters);
        }
    }

    async Task ActAsync(Guid account, Guid grant, string sql, params (string Name, object Value)[] parameters)
    {
        using (Accounts.ActingUnder(grant))
        {
            await Accounts.ExecuteAsync(App, account, sql, parameters);
        }
    }

    [Fact]
    public async Task A_proxy_sees_the_patients_record_under_their_grant_and_their_own_without_it()
    {
        var (clinician, patient, proxy) = await PeopleAsync();
        var stranger = await PersonAsync(clinician);
        var grant = await GrantAsync(patient.Account, patient, proxy);

        foreach (var connection in new[] { App, Read })
        {
            // Not acting: their own record, as before, though the grant exists.
            Assert.Equal([proxy.Patient.ToString()], await Accounts.QueryAsync(connection, proxy.Account, Seen));
            // Acting: the patient's, and not their own beside it.
            Assert.Equal([patient.Patient.ToString()], await ActingAsync(connection, proxy.Account, grant, Seen));
            Assert.Equal([patient.Patient.ToString()], await ActingAsync(connection, proxy.Account, grant, "SELECT id FROM ehr.patients"));
            Assert.Equal([patient.Patient.ToString()], await ActingAsync(connection, proxy.Account, grant, "SELECT ehr.current_patient_id()"));

            // The same grant named by anyone else opens nothing: another patient, an admin, nobody.
            Assert.Empty(await ActingAsync(connection, stranger.Account, grant, Seen));
            Assert.Empty(await ActingAsync(connection, await Accounts.StaffAsync(database, StaffRoles.Admin), grant, Seen));
            using (Accounts.ActingUnder(grant))
            {
                Assert.Empty(await Accounts.QueryAsync(connection, null, Seen));
            }

            // A grant that doesn't exist opens nothing, not the account's own record under another name.
            Assert.Empty(await ActingAsync(connection, proxy.Account, Guid.NewGuid(), Seen));
            // The patient is not their proxy's proxy.
            Assert.Equal([patient.Patient.ToString()], await Accounts.QueryAsync(connection, patient.Account, Seen));
            Assert.Empty(await ActingAsync(connection, patient.Account, grant, Seen));
        }
    }

    // The portal gets no medication entered in error from the database, whatever the app asks (the expansion plan's step
    // 13b, whose download hands a patient their medications): not the patient, and not a proxy allowed everything. A
    // clinician still does. Every other status reaches all three.
    [Theory]
    [InlineData("entered-in-error", false)]
    [InlineData("active", true)]
    [InlineData("stopped", true)]
    public async Task The_portal_gets_no_medication_entered_in_error(string status, bool portalSees)
    {
        var (clinician, patient, proxy) = await PeopleAsync();
        var grant = await GrantAsync(patient.Account, patient, proxy, sensitive: true);
        var id = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "INSERT INTO ehr.medication_statements (id, patient_id, display, status) VALUES (@id, @patient, 'Synthetic medication', @status)",
            ("id", id), ("patient", patient.Patient), ("status", status));
        const string count = "SELECT count(*) FROM ehr.medication_statements WHERE id = @id";
        var seen = portalSees ? "1" : "0";

        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal(["1"], await Accounts.QueryAsync(connection, clinician, count, ("id", id)));
            Assert.Equal([seen], await Accounts.QueryAsync(connection, patient.Account, count, ("id", id)));
            Assert.Equal([seen], await ActingAsync(connection, proxy.Account, grant, count, ("id", id)));
        }
    }

    // Step 6b: a portal account with no record of its own. By itself it reaches no patient data at all and can give
    // nobody access to anything; under a grant addressed to it, exactly what any proxy reaches; and when the grant
    // ends, nothing again.
    [Fact]
    public async Task A_proxy_only_account_reaches_only_the_record_shared_with_it()
    {
        var (clinician, patient, other) = await PeopleAsync();
        var (account, email) = await Accounts.ProxyOnlyAccountAsync(database);
        var proxy = new Person(Guid.Empty, account, email);
        foreach (var table in new[] { "conditions", "medication_statements", "allergy_intolerances", "encounters" })
        {
            await Accounts.ChartRowAsync(database, table, patient.Patient);
        }

        var thread = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, patient.Account, "INSERT INTO ehr.message_threads (id, patient_id, subject) VALUES (@id, @patient, 'Synthetic subject')",
            ("id", thread), ("patient", patient.Patient));
        var tables = new[]
        {
            "patients", "observations", "conditions", "medication_statements", "allergy_intolerances", "encounters", "appointments", "message_threads",
            "messages", "orders", "clinical_notes", "clinical_drafts", "patient_assignments", "break_glass_accesses", "inbox_items", "audit_events",
            "chart_history", "result_acknowledgments", "proxy_grants", "refill_requests",
        };
        var everything = string.Join(" + ", tables.Select(table => $"(SELECT count(*) FROM ehr.{table})"));

        foreach (var connection in new[] { App, Read })
        {
            // Nothing is shared with it yet: no row of any patient table, and no patient to be.
            Assert.Equal(["0"], await Accounts.QueryAsync(connection, account, $"SELECT {everything}"));
            Assert.Equal([""], await Accounts.QueryAsync(connection, account, "SELECT ehr.current_patient_id()"));
            Assert.Empty(await Accounts.QueryAsync(connection, account, "SELECT grant_id FROM ehr.proxied_records()"));
        }

        // It has no record to share, and can't share anyone else's or write anywhere.
        await RefusedAsync(() => GrantAsync(account, patient, other));
        await RefusedAsync(() => Accounts.ExecuteAsync(App, account, "INSERT INTO ehr.message_threads (id, patient_id, subject) VALUES (gen_random_uuid(), @patient, 'Synthetic')", ("patient", patient.Patient)));
        await RefusedAsync(() => Accounts.ExecuteAsync(App, account,
            "INSERT INTO ehr.messages (id, thread_id, patient_id, sender_kind, body) VALUES (gen_random_uuid(), @thread, @patient, 'patient', 'Synthetic')", ("thread", thread), ("patient", patient.Patient)));
        // Naming a grant that isn't addressed to it changes nothing.
        var notTheirs = await GrantAsync(patient.Account, patient, other);
        Assert.Equal(["0"], await ActingAsync(App, account, notTheirs, $"SELECT {everything}"));

        // Shared with it, for the record only: that patient's row and results, the grant itself, and no messages.
        var grant = await GrantAsync(clinician, patient, proxy);
        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal([$"{grant} {patient.Patient}"], await Accounts.QueryAsync(connection, account, "SELECT grant_id || ' ' || patient_id FROM ehr.proxied_records()"));
            Assert.Equal(["1"], await Accounts.QueryAsync(connection, account, $"SELECT {everything}"));
            Assert.Equal([patient.Patient.ToString()], await ActingAsync(connection, account, grant, "SELECT id FROM ehr.patients"));
            Assert.Equal([patient.Patient.ToString()], await ActingAsync(connection, account, grant, Seen));
            // Not the messages (the grant doesn't include them), and none of what no patient sees. Who is on the
            // patient's care team is the patient's to see, so it is the proxy's too: that one row.
            Assert.Equal(["0 1"], await ActingAsync(connection, account, grant,
                "SELECT (SELECT count(*) FROM ehr.message_threads) + (SELECT count(*) FROM ehr.orders) + (SELECT count(*) FROM ehr.audit_events) || ' ' || (SELECT count(*) FROM ehr.patient_assignments)"));
        }

        // Still nothing to pass on, acting or not.
        using (Accounts.ActingUnder(grant))
        {
            await RefusedAsync(() => GrantAsync(account, patient, other with { Email = "someone-else@example.com" }));
        }

        await Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.proxy_grants SET revoked_at = now() WHERE id = @id", ("id", grant));
        Assert.Equal(["0"], await ActingAsync(App, account, grant, $"SELECT {everything}"));
        Assert.Equal(["0"], await Accounts.QueryAsync(Read, account, $"SELECT {everything}"));
    }

    static async Task RefusedAsync(Func<Task> attempt)
    {
        var refused = await Assert.ThrowsAsync<PostgresException>(attempt);
        Assert.Contains(refused.SqlState, new[] { PostgresErrorCodes.InsufficientPrivilege, PostgresErrorCodes.CheckViolation });
    }

    // A proxy for two patients sees one record at a time: under one grant, nothing of the other, not even their row.
    [Fact]
    public async Task A_proxy_for_two_patients_sees_one_record_at_a_time()
    {
        var (clinician, first, proxy) = await PeopleAsync();
        var second = await PersonAsync(clinician);
        var firstGrant = await GrantAsync(first.Account, first, proxy);
        var secondGrant = await GrantAsync(second.Account, second, proxy);

        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal([first.Patient.ToString()], await ActingAsync(connection, proxy.Account, firstGrant, "SELECT id FROM ehr.patients"));
            Assert.Equal([first.Patient.ToString()], await ActingAsync(connection, proxy.Account, firstGrant, Seen));
            Assert.Equal([second.Patient.ToString()], await ActingAsync(connection, proxy.Account, secondGrant, "SELECT id FROM ehr.patients"));
            Assert.Equal([second.Patient.ToString()], await ActingAsync(connection, proxy.Account, secondGrant, Seen));
            Assert.Equal(2, (await Accounts.QueryAsync(connection, proxy.Account, "SELECT grant_id FROM ehr.proxied_records()")).Count);
        }
    }

    // The limit is on grants in force that the patient gave: expired ones and the clinic's don't use it up.
    [Fact]
    public async Task A_patient_gives_at_most_20_grants_in_force()
    {
        var (clinician, patient, _) = await PeopleAsync();
        Person Someone(int i) => new(Guid.Empty, Guid.Empty, $"synthetic-{i}-{patient.Account:N}@example.com");
        for (var i = 0; i < 20; i++)
        {
            await GrantAsync(patient.Account, patient, Someone(i));
        }

        var tooMany = await Assert.ThrowsAsync<PostgresException>(() => GrantAsync(patient.Account, patient, Someone(20)));
        Assert.Equal(PostgresErrorCodes.ProgramLimitExceeded, tooMany.SqlState);
        await GrantAsync(clinician, patient, Someone(21));
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "UPDATE ehr.proxy_grants SET expires_at = now() - interval '1 second' WHERE patient_id = @patient AND proxy_email LIKE 'SYNTHETIC-0-%'", ("patient", patient.Patient));
        await GrantAsync(patient.Account, patient, Someone(22));

        // And an address with a look-alike letter is not an address a grant can name.
        var lookAlike = await Assert.ThrowsAsync<PostgresException>(() => GrantAsync(clinician, patient, Someone(0) with { Email = "\u017fam@example.com" }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, lookAlike.SqlState);
    }

    [Fact]
    public async Task A_grant_that_is_revoked_expired_or_whose_account_is_unconfirmed_or_locked_opens_nothing()
    {
        var (_, patient, proxy) = await PeopleAsync();

        var revoked = await GrantAsync(patient.Account, patient, proxy);
        Assert.Single(await ActingAsync(App, proxy.Account, revoked, Seen));
        await Accounts.ExecuteAsync(App, patient.Account, "UPDATE ehr.proxy_grants SET revoked_at = '2000-01-01' WHERE id = @id", ("id", revoked));
        Assert.Empty(await ActingAsync(App, proxy.Account, revoked, Seen));
        Assert.Equal([$"{patient.Account} true"], await AsSuperuserAsync(
            "SELECT revoked_by || ' ' || (revoked_at > now() - interval '1 minute') FROM ehr.proxy_grants WHERE id = @id", ("id", revoked)));

        var expired = await GrantAsync(patient.Account, patient, proxy);
        Assert.Single(await ActingAsync(Read, proxy.Account, expired, Seen));
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null, "UPDATE ehr.proxy_grants SET expires_at = now() - interval '1 second' WHERE id = @id", ("id", expired));
        Assert.Empty(await ActingAsync(App, proxy.Account, expired, Seen));
        Assert.Empty(await ActingAsync(Read, proxy.Account, expired, Seen));
        Assert.Equal(["0"], await Accounts.QueryAsync(App, proxy.Account, "SELECT count(*) FROM ehr.proxy_grants"));
        Assert.Equal([proxy.Patient.ToString()], await Accounts.QueryAsync(App, proxy.Account, "SELECT id FROM ehr.patients"));

        await Accounts.ExecuteAsync(App, patient.Account, "UPDATE ehr.proxy_grants SET revoked_at = now() WHERE id = @id", ("id", expired));
        var live = await GrantAsync(patient.Account, patient, proxy);
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null, "UPDATE ehr.patient_users SET email_confirmed = false WHERE id = @id", ("id", proxy.Account));
        Assert.Empty(await ActingAsync(App, proxy.Account, live, Seen));
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null, "UPDATE ehr.patient_users SET email_confirmed = true, lockout_end = now() + interval '1 hour' WHERE id = @id", ("id", proxy.Account));
        Assert.Empty(await ActingAsync(App, proxy.Account, live, Seen));
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null, "UPDATE ehr.patient_users SET lockout_end = NULL WHERE id = @id", ("id", proxy.Account));
        Assert.Single(await ActingAsync(App, proxy.Account, live, Seen));
    }

    [Fact]
    public async Task A_proxy_sees_sensitive_items_only_when_the_patient_allows_it()
    {
        var (clinician, patient, proxy) = await PeopleAsync();
        foreach (var table in new[] { "conditions", "medication_statements" })
        {
            await Accounts.ChartRowAsync(database, table, patient.Patient);
            var marked = await Accounts.ChartRowAsync(database, table, patient.Patient);
            await Accounts.ExecuteAsync(database.SuperuserConnectionString, null, $"UPDATE ehr.{table} SET sensitive = true WHERE id = @id", ("id", marked));
        }

        const string counts = "SELECT (SELECT count(*) FROM ehr.conditions) || ' ' || (SELECT count(*) FROM ehr.medication_statements)";
        var grant = await GrantAsync(clinician, patient, proxy);
        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal(["2 2"], await Accounts.QueryAsync(connection, patient.Account, counts));
            Assert.Equal(["1 1"], await ActingAsync(connection, proxy.Account, grant, counts));
            Assert.Equal(["0"], await ActingAsync(connection, proxy.Account, grant, "SELECT count(*) FROM ehr.conditions WHERE sensitive"));
        }

        // Not the clinic, not the proxy: neither when recording the grant nor afterwards.
        const string allow = "UPDATE ehr.proxy_grants SET include_sensitive = true WHERE id = @id";
        var byClinic = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician, allow, ("id", grant)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, byClinic.SqlState);
        var recorded = await Assert.ThrowsAsync<PostgresException>(async () => await GrantAsync(clinician, patient, await PersonAsync(clinician), sensitive: true));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, recorded.SqlState);
        await Accounts.ExecuteAsync(App, proxy.Account, allow, ("id", grant));
        await ActAsync(proxy.Account, grant, allow, ("id", grant));
        Assert.Equal(["1 1"], await ActingAsync(App, proxy.Account, grant, counts));

        // The patient does, and it is recorded as theirs; and takes it back.
        await Accounts.ExecuteAsync(App, patient.Account, allow, ("id", grant));
        Assert.Equal(["2 2"], await ActingAsync(App, proxy.Account, grant, counts));
        Assert.Equal(["2 2"], await ActingAsync(Read, proxy.Account, grant, counts));
        Assert.Equal([patient.Account.ToString()], await AsSuperuserAsync(
            "SELECT actor_id::text FROM ehr.audit_events WHERE table_name = 'proxy_grants' AND row_id = @id AND action = 'update' AND patient_id = @patient",
            ("id", grant), ("patient", patient.Patient)));
        await Accounts.ExecuteAsync(App, patient.Account, "UPDATE ehr.proxy_grants SET include_sensitive = false WHERE id = @id", ("id", grant));
        Assert.Equal(["1 1"], await ActingAsync(App, proxy.Account, grant, counts));
    }

    [Fact]
    public async Task A_proxy_reads_and_sends_messages_only_when_the_grants_scope_includes_them_and_as_themselves()
    {
        var (clinician, patient, proxy) = await PeopleAsync();
        var thread = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, patient.Account, "INSERT INTO ehr.message_threads (id, patient_id, subject) VALUES (@id, @patient, 'Synthetic subject')",
            ("id", thread), ("patient", patient.Patient));
        const string send = "INSERT INTO ehr.messages (id, thread_id, patient_id, sender_kind, sender_id, body) VALUES (@id, @thread, @patient, 'patient', @sender, 'Synthetic: from the proxy')";
        const string threads = "SELECT (SELECT count(*) FROM ehr.message_threads) + (SELECT count(*) FROM ehr.messages)";

        var recordOnly = await GrantAsync(patient.Account, patient, proxy);
        Assert.Equal(["0"], await ActingAsync(App, proxy.Account, recordOnly, threads));
        var refused = await Assert.ThrowsAsync<PostgresException>(() => ActAsync(proxy.Account, recordOnly, send,
            ("id", Guid.CreateVersion7()), ("thread", thread), ("patient", patient.Patient), ("sender", patient.Account)));
        Assert.Contains(refused.SqlState, new[] { PostgresErrorCodes.InsufficientPrivilege, PostgresErrorCodes.CheckViolation });

        await Accounts.ExecuteAsync(App, patient.Account, "UPDATE ehr.proxy_grants SET revoked_at = now() WHERE id = @id", ("id", recordOnly));
        var withMessages = await GrantAsync(patient.Account, patient, proxy, scope: "record-and-messages");
        Assert.Equal(["1"], await ActingAsync(App, proxy.Account, withMessages, threads));
        var message = Guid.CreateVersion7();
        // Claiming to be the patient's own account changes nothing: the message is the proxy's.
        await ActAsync(proxy.Account, withMessages, send, ("id", message), ("thread", thread), ("patient", patient.Patient), ("sender", patient.Account));
        Assert.Equal([proxy.Account.ToString()], await AsSuperuserAsync("SELECT sender_id::text FROM ehr.messages WHERE id = @id", ("id", message)));
        Assert.Equal([clinician.ToString()], await AsSuperuserAsync("SELECT recipient_id::text FROM ehr.inbox_items WHERE source_id = @thread", ("thread", thread)));
        // The audit names both: the proxy's account did it, in the patient's record.
        Assert.Equal([$"{proxy.Account} {patient.Patient}"], await AsSuperuserAsync(
            "SELECT actor_id || ' ' || patient_id FROM ehr.audit_events WHERE table_name = 'messages' AND row_id = @id", ("id", message)));

        // Reading as a proxy never marks the care team's messages read: the patient still finds them new.
        var reply = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician, "INSERT INTO ehr.messages (id, thread_id, patient_id, sender_kind, body) VALUES (@id, @thread, @patient, 'staff', 'Synthetic: a reply')",
            ("id", reply), ("thread", thread), ("patient", patient.Patient));
        await ActAsync(proxy.Account, withMessages, "UPDATE ehr.messages SET read_at = now() WHERE id = @id", ("id", reply));
        Assert.Equal(["true"], await AsSuperuserAsync("SELECT (read_at IS NULL)::text FROM ehr.messages WHERE id = @id", ("id", reply)));
        await Accounts.ExecuteAsync(App, patient.Account, "UPDATE ehr.messages SET read_at = now() WHERE id = @id", ("id", reply));
        Assert.Equal(["false"], await AsSuperuserAsync("SELECT (read_at IS NULL)::text FROM ehr.messages WHERE id = @id", ("id", reply)));

        // Not into their own record while acting, and not into the patient's while not.
        var own = await Assert.ThrowsAsync<PostgresException>(() => ActAsync(proxy.Account, withMessages,
            "INSERT INTO ehr.message_threads (id, patient_id, subject) VALUES (gen_random_uuid(), @patient, 'Synthetic')", ("patient", proxy.Patient)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, own.SqlState);
        var notActing = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, proxy.Account, send,
            ("id", Guid.CreateVersion7()), ("thread", thread), ("patient", patient.Patient), ("sender", proxy.Account)));
        Assert.Contains(notActing.SqlState, new[] { PostgresErrorCodes.InsufficientPrivilege, PostgresErrorCodes.CheckViolation });
    }

    [Fact]
    public async Task Only_the_patient_and_their_care_team_give_a_grant_and_the_database_sets_who_and_when()
    {
        var (clinician, patient, proxy) = await PeopleAsync();
        var offPanel = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var third = await PersonAsync(clinician);

        var byPatient = await GrantAsync(patient.Account, patient, proxy);
        var byClinic = await GrantAsync(clinician, patient, third);
        Assert.Equal([$"patient {patient.Account} true {proxy.Email.ToUpperInvariant()}", $"staff {clinician} true {third.Email.ToUpperInvariant()}"], await AsSuperuserAsync(
            "SELECT granted_by_kind || ' ' || granted_by || ' ' || (granted_at > now() - interval '1 minute') || ' ' || proxy_email FROM ehr.proxy_grants WHERE patient_id = @patient ORDER BY granted_by_kind",
            ("patient", patient.Patient)));

        // Not for someone else's record, not off the panel, not an admin, not nobody, not the proxy acting for the patient.
        foreach (var by in new Guid?[] { proxy.Account, third.Account, offPanel, admin, null })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(async () => await GrantAsync(by, patient, await PersonAsync(clinician)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        using (Accounts.ActingUnder(byPatient))
        {
            var passedOn = await Assert.ThrowsAsync<PostgresException>(() => GrantAsync(proxy.Account, patient, third));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, passedOn.SqlState);
        }

        var readRole = await Assert.ThrowsAsync<PostgresException>(async () => await GrantAsync(patient.Account, patient, await PersonAsync(clinician), connection: Read));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, readRole.SqlState);

        // Its shape: in force once per proxy, expiring within a year, never oneself.
        var twice = await Assert.ThrowsAsync<PostgresException>(() => GrantAsync(patient.Account, patient, proxy));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, twice.SqlState);
        foreach (var days in new[] { 0, -1, 367 })
        {
            var expiry = await Assert.ThrowsAsync<PostgresException>(async () => await GrantAsync(patient.Account, patient, await PersonAsync(clinician), days: days));
            Assert.Equal(PostgresErrorCodes.CheckViolation, expiry.SqlState);
        }

        var self = await Assert.ThrowsAsync<PostgresException>(() => GrantAsync(patient.Account, patient, patient));
        Assert.Equal(PostgresErrorCodes.CheckViolation, self.SqlState);
        var notAnEmail = await Assert.ThrowsAsync<PostgresException>(() => GrantAsync(patient.Account, patient, proxy with { Email = "not an email" }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, notAnEmail.SqlState);
        Assert.Equal(["2"], await AsSuperuserAsync("SELECT count(*) FROM ehr.proxy_grants WHERE patient_id = @patient", ("patient", patient.Patient)));
        _ = byClinic;
    }

    [Fact]
    public async Task A_grant_is_revoked_by_the_patient_or_their_care_team_and_never_edited_or_deleted()
    {
        var (clinician, patient, proxy) = await PeopleAsync();
        var third = await PersonAsync(clinician);
        var offPanel = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var first = await GrantAsync(patient.Account, patient, proxy);
        var second = await GrantAsync(patient.Account, patient, third);
        const string revoke = "UPDATE ehr.proxy_grants SET revoked_at = now() WHERE id = @id";

        // Nobody else's to revoke: the proxy (acting or not), another proxy, a clinician off the panel. No row to update.
        await Accounts.ExecuteAsync(App, proxy.Account, revoke, ("id", first));
        await ActAsync(proxy.Account, first, revoke, ("id", first));
        await Accounts.ExecuteAsync(App, third.Account, revoke, ("id", first));
        await Accounts.ExecuteAsync(App, offPanel, revoke, ("id", first));
        Assert.Equal(["2"], await AsSuperuserAsync("SELECT count(*) FROM ehr.proxy_grants WHERE patient_id = @patient AND revoked_at IS NULL", ("patient", patient.Patient)));

        await Accounts.ExecuteAsync(App, clinician, revoke, ("id", first));
        await Accounts.ExecuteAsync(App, patient.Account, revoke, ("id", second));
        Assert.Equal([clinician.ToString(), patient.Account.ToString()], await AsSuperuserAsync(
            "SELECT revoked_by::text FROM ehr.proxy_grants WHERE patient_id = @patient ORDER BY id", ("patient", patient.Patient)));
        var undone = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, patient.Account,
            "UPDATE ehr.proxy_grants SET revoked_at = NULL WHERE id = @id", ("id", second)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, undone.SqlState);

        var third2 = await GrantAsync(patient.Account, patient, proxy);
        foreach (var sql in new[]
        {
            "UPDATE ehr.proxy_grants SET scope = 'record-and-messages' WHERE id = @id",
            "UPDATE ehr.proxy_grants SET expires_at = now() + interval '300 days' WHERE id = @id",
            "UPDATE ehr.proxy_grants SET proxy_email = 'SOMEONE@EXAMPLE.COM' WHERE id = @id",
            "UPDATE ehr.proxy_grants SET patient_id = gen_random_uuid() WHERE id = @id",
            "DELETE FROM ehr.proxy_grants WHERE id = @id",
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, patient.Account, sql, ("id", third2)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
    }

    [Fact]
    public async Task Grants_are_read_by_the_patient_the_proxy_and_clinicians_only()
    {
        var (clinician, patient, proxy) = await PeopleAsync();
        var stranger = await PersonAsync(clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var grant = await GrantAsync(patient.Account, patient, proxy);
        const string read = "SELECT count(*) FROM ehr.proxy_grants WHERE id = @id";

        foreach (var connection in new[] { App, Read })
        {
            foreach (var allowed in new[] { patient.Account, proxy.Account, clinician, await Accounts.StaffAsync(database, StaffRoles.Clinician) })
            {
                Assert.Equal(["1"], await Accounts.QueryAsync(connection, allowed, read, ("id", grant)));
            }

            foreach (var refused in new Guid?[] { stranger.Account, admin, null, Guid.NewGuid() })
            {
                Assert.Equal(["0"], await Accounts.QueryAsync(connection, refused, read, ("id", grant)));
            }

            // Before choosing, the proxy is told whose record it is, by name, and reads no row of theirs.
            Assert.Equal([proxy.Patient.ToString()], await Accounts.QueryAsync(connection, proxy.Account, "SELECT id FROM ehr.patients"));
            Assert.Equal([$"{grant} {patient.Patient}"], await Accounts.QueryAsync(connection, proxy.Account, "SELECT grant_id || ' ' || patient_id FROM ehr.proxied_records()"));
            foreach (var other in new Guid?[] { stranger.Account, patient.Account, clinician, admin, null })
            {
                Assert.Empty(await Accounts.QueryAsync(connection, other, "SELECT grant_id FROM ehr.proxied_records()"));
            }
        }
    }
}
