using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;
using static Ehr.Testing.TestRun;

namespace Ehr.Security.Tests;

// The expansion plan's step 18 (UC 24, proposed FR-64): what the database holds an emergency access's review to, as the
// runtime roles, whatever the app does (db/policies/72-emergency-access-review.sql, 30-rls.sql, 70-inbox.sql). Each
// grant reaches every administrator but the clinician who used it and the disabled; a failure there never refuses the
// access, leaves a trace with no patient data, and the migration job's sweep makes up for it; a review is an
// administrator's, not the clinician's own, once the access has ended, with an outcome and a note, once and for good,
// its who and when the database's; it closes every administrator's item, which nothing else does; only administrators
// read reviews, and the access's times only through the function made for them.
public class EmergencyAccessReviewSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    const string Note = "Synthetic: confirmed with the ward that the patient was being seen.";

    // An emergency access the clinician granted themselves through the app role, as BreakGlass.GrantAsync does, ended or
    // still in force.
    async Task<(Guid Access, Guid Patient)> AccessAsync(Guid clinician, bool ended = true)
    {
        var owner = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, owner);
        var access = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician,
            """
            INSERT INTO ehr.break_glass_accesses (id, patient_id, clinician_id, reason, granted_at, expires_at)
            VALUES (@id, @patient, @clinician, 'Synthetic: covering the ward', now() - @back, now() - @back + interval '4 hours')
            """,
            ("id", access), ("patient", patient), ("clinician", clinician), ("back", ended ? TimeSpan.FromHours(5) : TimeSpan.Zero));
        return (access, patient);
    }

    Task<List<string>> AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.QueryAsync(database.SuperuserConnectionString, null, sql, parameters);

    // Each recipient of an item for the access, with whether it's done, in no particular order.
    async Task<Dictionary<Guid, bool>> ItemsAsync(Guid access) =>
        (await AsSuperuserAsync(
            "SELECT recipient_id::text || ' ' || (done_at IS NOT NULL)::text FROM ehr.inbox_items WHERE source_table = 'break_glass_accesses' AND source_id = @id",
            ("id", access)))
        .Select(row => row.Split(' ')).ToDictionary(row => Guid.Parse(row[0]), row => bool.Parse(row[1]));

    Task ReviewAsync(Guid admin, Guid access, string note = Note, string outcome = "appropriate", string connection = "") =>
        Accounts.ExecuteAsync(connection.Length > 0 ? connection : App, admin,
            "INSERT INTO ehr.emergency_access_reviews (id, reviewer_id, reviewed_at, outcome, note) VALUES (@id, @reviewer, '2000-01-01', @outcome, @note)",
            ("id", access), ("reviewer", admin), ("outcome", outcome), ("note", note));

    static async Task<PostgresException> RefusedAsync(Func<Task> act) => await Assert.ThrowsAsync<PostgresException>(act);

    // The migration job's run of the step's policies, the sweep at its end.
    Task SweepAsync() => MigratedDatabase.ExecuteAsync(database.OwnerConnectionString,
        File.ReadAllText(RepositoryRoot.Combine("db/policies/72-emergency-access-review.sql")));

    [Fact]
    public async Task A_grant_reaches_every_administrator_but_its_clinician_and_the_disabled()
    {
        var first = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var second = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var disabled = await Accounts.StaffAsync(database, StaffRoles.Admin);
        await Accounts.LockOutAsync(database, disabled);
        var adminClinician = await Accounts.StaffAsync(database, StaffRoles.Clinician, StaffRoles.Admin);

        var (access, patient) = await AccessAsync(adminClinician, ended: false);
        var items = await ItemsAsync(access);
        Assert.False(items[first]);
        Assert.False(items[second]);
        Assert.DoesNotContain(disabled, items.Keys);
        Assert.DoesNotContain(adminClinician, items.Keys);
        Assert.Equal(["emergency-access-review A clinician used emergency access to a chart " + patient],
            (await AsSuperuserAsync(
                "SELECT DISTINCT kind || ' ' || summary || ' ' || patient_id FROM ehr.inbox_items WHERE source_id = @id", ("id", access))));
    }

    [Fact]
    public async Task A_failure_to_tell_the_administrators_never_refuses_the_access_and_the_sweep_makes_up_for_it()
    {
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "ALTER TABLE ehr.inbox_items ADD CONSTRAINT synthetic_refuse_review_items CHECK (kind <> 'emergency-access-review') NOT VALID");
        Guid access;
        try
        {
            (access, _) = await AccessAsync(clinician);
        }
        finally
        {
            await Accounts.ExecuteAsync(database.SuperuserConnectionString, null, "ALTER TABLE ehr.inbox_items DROP CONSTRAINT synthetic_refuse_review_items");
        }

        // Granted, with nothing delivered, and a trace with no patient in it.
        Assert.Equal(["1"], await AsSuperuserAsync("SELECT count(*) FROM ehr.break_glass_accesses WHERE id = @id", ("id", access)));
        Assert.Empty(await ItemsAsync(access));
        Assert.Equal([$"review_items_failed break_glass_accesses {clinician}  23514"], await AsSuperuserAsync(
            "SELECT action || ' ' || table_name || ' ' || actor_id || ' ' || coalesce(patient_id::text, '') || ' ' || reason FROM ehr.audit_events WHERE row_id = @id AND action <> 'insert'",
            ("id", access)));

        await SweepAsync();
        Assert.False((await ItemsAsync(access))[admin]);
    }

    [Fact]
    public async Task A_review_is_an_administrators_not_the_clinicians_own_once_the_access_has_ended()
    {
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var adminClinician = await Accounts.StaffAsync(database, StaffRoles.Clinician, StaffRoles.Admin);
        var (inForce, _) = await AccessAsync(clinician, ended: false);
        var (ended, patient) = await AccessAsync(clinician);
        var (own, _) = await AccessAsync(adminClinician);
        var patientAccount = await Accounts.PatientAccountAsync(database, patient);

        Assert.Equal("emergency_access_reviews_ended", (await RefusedAsync(() => ReviewAsync(admin, inForce))).ConstraintName);
        Assert.Equal("emergency_access_reviews_own", (await RefusedAsync(() => ReviewAsync(adminClinician, own))).ConstraintName);
        foreach (var who in new Guid?[] { clinician, patientAccount, null })
        {
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await RefusedAsync(() => Accounts.ExecuteAsync(App, who,
                "INSERT INTO ehr.emergency_access_reviews (id, reviewer_id, reviewed_at, outcome, note) VALUES (@id, @admin, now(), 'appropriate', @note)",
                ("id", ended), ("admin", admin), ("note", Note)))).SqlState);
        }

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await RefusedAsync(() => ReviewAsync(admin, ended, connection: Read))).SqlState);
        Assert.Equal("emergency_access_reviews_access", (await RefusedAsync(() => ReviewAsync(admin, Guid.CreateVersion7()))).ConstraintName);
        foreach (var note in new[] { "Too short", "   too short   " })
        {
            Assert.Equal("emergency_access_reviews_note", (await RefusedAsync(() => ReviewAsync(admin, ended, note))).ConstraintName);
        }

        // Longer than the column takes: refused as the value is read, before the guard.
        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, (await RefusedAsync(() => ReviewAsync(admin, ended, new string('x', 501)))).SqlState);

        Assert.Equal("ck_emergency_access_reviews_outcome", (await RefusedAsync(() => ReviewAsync(admin, ended, outcome: "fine"))).ConstraintName);

        // The reviewer and the time are the database's, whatever was sent (2000-01-01); the note is kept trimmed.
        await ReviewAsync(admin, ended, "  " + Note + "  ", "not-appropriate");
        Assert.Equal([$"{admin} not-appropriate {Note} true"], await AsSuperuserAsync(
            "SELECT reviewer_id || ' ' || outcome || ' ' || note || ' ' || (reviewed_at > now() - interval '1 minute') FROM ehr.emergency_access_reviews WHERE id = @id",
            ("id", ended)));

        // Once.
        var another = await Accounts.StaffAsync(database, StaffRoles.Admin);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, (await RefusedAsync(() => ReviewAsync(another, ended))).SqlState);
    }

    [Fact]
    public async Task A_review_is_never_changed_or_deleted_by_anyone()
    {
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var (access, _) = await AccessAsync(await Accounts.StaffAsync(database, StaffRoles.Clinician));
        await ReviewAsync(admin, access);

        foreach (var sql in new[] { "UPDATE ehr.emergency_access_reviews SET note = note || ' changed' WHERE id = @id", "DELETE FROM ehr.emergency_access_reviews WHERE id = @id" })
        {
            // The app role has no grant; the tables' owner has no policy to change one, so finds none; and the trigger
            // refuses even a role that row-level security doesn't bind.
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await RefusedAsync(() => Accounts.ExecuteAsync(App, admin, sql, ("id", access)))).SqlState);
            await Accounts.ExecuteAsync(database.OwnerConnectionString, admin, sql, ("id", access));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await RefusedAsync(() => Accounts.ExecuteAsync(database.SuperuserConnectionString, null, sql, ("id", access)))).SqlState);
        }

        Assert.Equal([Note], await AsSuperuserAsync("SELECT note FROM ehr.emergency_access_reviews WHERE id = @id", ("id", access)));
    }

    [Fact]
    public async Task A_review_closes_every_administrators_item_and_nothing_else_does()
    {
        var first = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var second = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var (access, patient) = await AccessAsync(await Accounts.StaffAsync(database, StaffRoles.Clinician));
        var (other, _) = await AccessAsync(await Accounts.StaffAsync(database, StaffRoles.Clinician));

        var refused = await RefusedAsync(() => Accounts.ExecuteAsync(App, first,
            "UPDATE ehr.inbox_items SET done_at = now() WHERE recipient_id = @me AND source_id = @id", ("me", first), ("id", access)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);

        await ReviewAsync(second, access);
        var items = await ItemsAsync(access);
        Assert.True(items[first]);
        Assert.True(items[second]);
        Assert.False((await ItemsAsync(other))[first]);

        // An item of the kind with no access behind it is marked done as any other (TC-UC15-02 adds one).
        var loose = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "INSERT INTO ehr.inbox_items (id, recipient_id, kind, patient_id, summary, created_at) VALUES (@id, @me, 'emergency-access-review', @patient, 'Synthetic', now())",
            ("id", loose), ("me", first), ("patient", patient));
        await Accounts.ExecuteAsync(App, first, "UPDATE ehr.inbox_items SET done_at = now() WHERE id = @id", ("id", loose));
        Assert.Equal(["true"], await AsSuperuserAsync("SELECT (done_at IS NOT NULL)::text FROM ehr.inbox_items WHERE id = @id", ("id", loose)));
    }

    [Fact]
    public async Task Only_administrators_read_reviews_and_an_accesss_times_only_through_its_function()
    {
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var lockedAdmin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        await Accounts.LockOutAsync(database, lockedAdmin);
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var (access, patient) = await AccessAsync(clinician);
        var patientAccount = await Accounts.PatientAccountAsync(database, patient);
        await ReviewAsync(admin, access);

        const string reviews = "SELECT count(*) FROM ehr.emergency_access_reviews WHERE id = @id";
        const string function = "SELECT patient_id || ' ' || clinician_id || ' ' || reason || ' ' || (expires_at - granted_at) FROM ehr.emergency_access_for_review(@id)";
        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal(["1"], await Accounts.QueryAsync(connection, admin, reviews, ("id", access)));
            Assert.Equal([$"{patient} {clinician} Synthetic: covering the ward 04:00:00"], await Accounts.QueryAsync(connection, admin, function, ("id", access)));
            foreach (var who in new Guid?[] { clinician, patientAccount, lockedAdmin, null })
            {
                Assert.Equal(["0"], await Accounts.QueryAsync(connection, who, reviews, ("id", access)));
                Assert.Empty(await Accounts.QueryAsync(connection, who, function, ("id", access)));
            }

            // An administrator still reads no emergency access directly: it is patient data.
            Assert.Equal(["0"], await Accounts.QueryAsync(connection, admin, "SELECT count(*) FROM ehr.break_glass_accesses WHERE id = @id", ("id", access)));
        }
    }

    [Fact]
    public async Task The_sweep_gives_each_waiting_access_to_each_administrator_once_and_never_a_reviewed_one()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        // Two accesses from before this step, with no items: the delivery is off while they are written.
        var waiting = Guid.CreateVersion7();
        var reviewed = Guid.CreateVersion7();
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            """
            SET LOCAL session_replication_role = replica;
            INSERT INTO ehr.break_glass_accesses (id, patient_id, clinician_id, reason, granted_at, expires_at)
            VALUES (@waiting, @patient, @clinician, 'Synthetic: before step 18', now() - interval '2 days', now() - interval '2 days' + interval '4 hours'),
                   (@reviewed, @patient, @clinician, 'Synthetic: before step 18', now() - interval '2 days', now() - interval '2 days' + interval '4 hours')
            """,
            ("waiting", waiting), ("reviewed", reviewed), ("patient", patient), ("clinician", clinician));
        await ReviewAsync(admin, reviewed);
        Assert.Empty(await ItemsAsync(waiting));
        var later = await Accounts.StaffAsync(database, StaffRoles.Admin);

        await SweepAsync();
        var items = await ItemsAsync(waiting);
        Assert.False(items[admin]);
        Assert.False(items[later]);
        Assert.DoesNotContain(clinician, items.Keys);
        Assert.Empty(await ItemsAsync(reviewed));

        await SweepAsync();
        Assert.Equal(items.Count, (await ItemsAsync(waiting)).Count);
    }

    // The sweep and a review at the same moment: the review waits for the sweep, or the sweep for the review, and either
    // way no item is left open for an access that has its review.
    [Fact]
    public async Task A_review_added_while_the_sweep_runs_leaves_no_item_open()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var (access, _) = await AccessAsync(clinician);
        var latecomer = await Accounts.StaffAsync(database, StaffRoles.Admin);

        await using var reviewing = new NpgsqlConnection(App);
        await reviewing.OpenAsync(Cancel);
        await using var transaction = await reviewing.BeginTransactionAsync(Cancel);
        await using (var user = new NpgsqlCommand("SELECT set_config('app.user_id', @id, true)", reviewing, transaction))
        {
            user.Parameters.AddWithValue("id", admin.ToString());
            await user.ExecuteNonQueryAsync(Cancel);
        }

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO ehr.emergency_access_reviews (id, reviewer_id, reviewed_at, outcome, note) VALUES (@id, @admin, now(), 'appropriate', @note)",
            reviewing, transaction))
        {
            insert.Parameters.AddWithValue("id", access);
            insert.Parameters.AddWithValue("admin", admin);
            insert.Parameters.AddWithValue("note", Note);
            await insert.ExecuteNonQueryAsync(Cancel);
        }

        var sweep = SweepAsync();
        await Task.Delay(1000, Cancel);
        Assert.False(sweep.IsCompleted);
        await transaction.CommitAsync(Cancel);
        await sweep;

        Assert.DoesNotContain(latecomer, (await ItemsAsync(access)).Keys);
        Assert.All((await ItemsAsync(access)).Values, Assert.True);
    }
}
