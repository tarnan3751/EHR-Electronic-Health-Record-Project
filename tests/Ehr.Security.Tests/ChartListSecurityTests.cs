using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// The expansion plan's step 2: what the database lets each account do with a patient's allergies, medications and
// diagnoses, as the runtime roles, in SQL of its own, so these hold whatever the app checks (db/policies/10-grants.sql,
// 30-rls.sql and 65-chart-lists.sql). A clinician adds and changes entries only for a patient on their active panel;
// nothing deletes one or moves one to another patient; the database keeps the history of every change, which nobody
// at runtime can write; and a diagnosis in a sensitive range is sensitive whatever was sent.
public class ChartListSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    static readonly Dictionary<string, (string Insert, string Update)> Statements = new()
    {
        ["allergy_intolerances"] = (
            "INSERT INTO ehr.allergy_intolerances (id, patient_id, code, code_system, display, clinical_status, recorded_at) VALUES (@id, @patient, 'QE1QX6B99R', 'unii', 'Peanut', 'active', now())",
            "UPDATE ehr.allergy_intolerances SET clinical_status = 'inactive' WHERE id = @id"),
        ["medication_statements"] = (
            "INSERT INTO ehr.medication_statements (id, patient_id, code, display, status) VALUES (@id, @patient, '314076', 'lisinopril 10 MG Oral Tablet', 'active')",
            "UPDATE ehr.medication_statements SET status = 'stopped' WHERE id = @id"),
        ["conditions"] = (
            "INSERT INTO ehr.conditions (id, patient_id, code, display, clinical_status) VALUES (@id, @patient, 'I10', 'Essential (primary) hypertension', 'active')",
            "UPDATE ehr.conditions SET clinical_status = 'resolved' WHERE id = @id"),
    };

    public static TheoryData<string> Tables => [.. Statements.Keys];

    Task<(Guid Clinician, Guid Patient)> OnPanelAsync() => Accounts.OnPanelAsync(database);

    Task<List<string>> AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.QueryAsync(database.SuperuserConnectionString, null, sql, parameters);

    [Theory]
    [MemberData(nameof(Tables))]
    public async Task A_clinician_adds_and_changes_entries_for_a_patient_on_their_panel_and_the_history_and_audit_record_both(string table)
    {
        var (clinician, patient) = await OnPanelAsync();
        var id = Guid.CreateVersion7();

        await Accounts.ExecuteAsync(App, clinician, Statements[table].Insert, ("id", id), ("patient", patient));
        await Accounts.ExecuteAsync(App, clinician, Statements[table].Update, ("id", id));
        // An update that changes nothing adds no history.
        await Accounts.ExecuteAsync(App, clinician, Statements[table].Update, ("id", id));

        // The history, as a clinician reads it: added, then changed, each by this clinician, with the entry before and after.
        Assert.Equal([$"{clinician} {patient} true", $"{clinician} {patient} false"], await Accounts.QueryAsync(App, clinician,
            """
            SELECT changed_by_id || ' ' || patient_id || ' ' || (before IS NULL) FROM ehr.chart_history
            WHERE table_name = @table AND row_id = @id ORDER BY changed_at
            """,
            ("table", table), ("id", id)));
        Assert.Equal(["true"], await AsSuperuserAsync(
            "SELECT (before <> after)::text FROM ehr.chart_history WHERE row_id = @id AND before IS NOT NULL", ("id", id)));
        // The write audit names the clinician for the insert and for each update (it records the one that changed nothing too).
        Assert.Equal([$"insert {clinician} 1", $"update {clinician} 2"], await AsSuperuserAsync(
            "SELECT action || ' ' || actor_id || ' ' || count(*) FROM ehr.audit_events WHERE table_name = @table AND row_id = @id AND patient_id = @patient GROUP BY action, actor_id ORDER BY action",
            ("table", table), ("id", id), ("patient", patient)));
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public async Task Nobody_but_a_clinician_with_the_patient_on_their_panel_adds_or_changes_an_entry(string table)
    {
        var (clinician, patient) = await OnPanelAsync();
        var existing = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician, Statements[table].Insert, ("id", existing), ("patient", patient));
        var offPanel = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var ended = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, ended, patient);
        await Accounts.ExecuteAsync(App, ended, "UPDATE ehr.patient_assignments SET ended_at = now() WHERE clinician_id = @id", ("id", ended));
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var patientAccount = await Accounts.PatientAccountAsync(database, patient);
        var disabled = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, disabled, patient);
        await Accounts.LockOutAsync(database, disabled);

        foreach (var refused in new Guid?[] { offPanel, ended, admin, patientAccount, disabled, null, Guid.NewGuid() })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                Accounts.ExecuteAsync(App, refused, Statements[table].Insert, ("id", Guid.CreateVersion7()), ("patient", patient)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            // Row-level security shows them no entry to update: nothing changes.
            await Accounts.ExecuteAsync(App, refused, Statements[table].Update, ("id", existing));
        }

        // The cloud app's role writes nothing, a clinician's context or not.
        foreach (var sql in new[] { Statements[table].Insert, Statements[table].Update })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                Accounts.ExecuteAsync(Read, clinician, sql, ("id", existing), ("patient", patient)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        Assert.Equal(["1"], await AsSuperuserAsync($"SELECT count(*) FROM ehr.{table} WHERE patient_id = @patient", ("patient", patient)));
        Assert.Equal(["1"], await AsSuperuserAsync("SELECT count(*) FROM ehr.chart_history WHERE row_id = @id", ("id", existing)));
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public async Task An_entry_is_never_deleted_or_moved_to_another_patient(string table)
    {
        var (clinician, patient) = await OnPanelAsync();
        var other = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, other);
        var id = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician, Statements[table].Insert, ("id", id), ("patient", patient));

        foreach (var sql in new[]
        {
            $"DELETE FROM ehr.{table} WHERE id = @id",
            $"UPDATE ehr.{table} SET patient_id = @other WHERE id = @id",
            $"UPDATE ehr.{table} SET id = @other WHERE id = @id",
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician, sql, ("id", id), ("other", other)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        Assert.Equal([patient.ToString()], await AsSuperuserAsync($"SELECT patient_id::text FROM ehr.{table} WHERE id = @id", ("id", id)));
    }

    // The rule in db/policies/65-chart-lists.sql and the plan's decision log.
    [Theory]
    [InlineData("F41.1", true)]
    [InlineData("F10.20", true)]
    [InlineData("f32.a", true)]
    [InlineData("F99", true)]
    [InlineData("A54.00", true)]
    [InlineData("A64", true)]
    [InlineData("A49.9", false)]
    [InlineData("A65", false)]
    [InlineData("B20", true)]
    [InlineData("Z21", true)]
    [InlineData("Z33.2", true)]
    [InlineData("Z33.1", false)]
    [InlineData("O04.80", true)]
    [InlineData("T74.11XA", true)]
    [InlineData("T76.21XA", true)]
    [InlineData("X78.1XXA", true)]
    [InlineData("X70.XXXA", false)]
    [InlineData("R45.851", true)]
    [InlineData("R45.0", false)]
    // Step 2b: suicide attempt, history of suicidal behavior and of self-harm, and poisoning or a toxic effect by
    // intentional self-harm, where the 2 is the sixth character or, in a four-character subcategory, the fifth.
    [InlineData("T14.91XA", true)]
    [InlineData("T14.90XA", false)]
    [InlineData("Z91.51", true)]
    [InlineData("Z91.52", true)]
    [InlineData("Z91.5", false)]
    [InlineData("Z91.81", false)]
    [InlineData("T39.012A", true)]
    [InlineData("T39.011A", false)]
    [InlineData("T39.013A", false)]
    [InlineData("T36.92XA", true)]
    [InlineData("T36.91XA", false)]
    [InlineData("T50.902D", true)]
    [InlineData("T65.92XS", true)]
    [InlineData("T35.2", false)]
    [InlineData("T66.XXXA", false)]
    [InlineData(" t43.212a ", true)]
    // After step 2b's review: nonsuicidal self-harm, and asphyxiation by intentional self-harm, where the 2 is the
    // sixth character of a T71.1 or T71.2 code. The accidental, assault and undetermined codes beside them are not.
    [InlineData("R45.88", true)]
    [InlineData("r4588", true)]
    [InlineData("R45.87", false)]
    [InlineData("R45.89", false)]
    [InlineData("T71.112A", true)]
    [InlineData("T71.162S", true)]
    [InlineData("T71.192D", true)]
    [InlineData("T71.222A", true)]
    [InlineData("T71.232S", true)]
    [InlineData("T71.111A", false)]
    [InlineData("T71.113A", false)]
    [InlineData("T71.114A", false)]
    [InlineData("T71.221A", false)]
    [InlineData("T71.20XA", false)]
    [InlineData("T71.9XXA", false)]
    [InlineData("T70.3XXA", false)]
    [InlineData("I10", false)]
    [InlineData("E11.9", false)]
    [InlineData("", false)]
    public async Task The_sensitive_ranges_are_the_ones_the_plan_records(string code, bool sensitive)
    {
        Assert.Equal([sensitive.ToString().ToLowerInvariant()], await Accounts.QueryAsync(App, null,
            "SELECT ehr.icd10cm_is_sensitive(@code)::text", ("code", code)));
    }

    [Fact]
    public async Task A_diagnosis_in_a_sensitive_range_is_sensitive_whatever_was_sent_and_any_other_is_as_marked()
    {
        var (clinician, patient) = await OnPanelAsync();
        var anxiety = Guid.CreateVersion7();
        var hypertension = Guid.CreateVersion7();
        const string insert = "INSERT INTO ehr.conditions (id, patient_id, code, display, clinical_status, sensitive) VALUES (@id, @patient, @code, 'Synthetic', 'active', false)";
        await Accounts.ExecuteAsync(App, clinician, insert, ("id", anxiety), ("patient", patient), ("code", "F41.1"));
        await Accounts.ExecuteAsync(App, clinician, insert, ("id", hypertension), ("patient", patient), ("code", "I10"));
        Task<List<string>> FlagsAsync() => AsSuperuserAsync(
            "SELECT code || ' ' || sensitive FROM ehr.conditions WHERE patient_id = @patient ORDER BY code", ("patient", patient));

        Assert.Equal(["F41.1 true", "I10 false"], await FlagsAsync());

        // Clearing it for a sensitive code does nothing; a clinician may mark any other, and unmark it.
        await Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.conditions SET sensitive = false WHERE id = @id", ("id", anxiety));
        await Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.conditions SET sensitive = true WHERE id = @id", ("id", hypertension));
        Assert.Equal(["F41.1 true", "I10 true"], await FlagsAsync());
        await Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.conditions SET sensitive = false WHERE id = @id", ("id", hypertension));
        // A code changed into a sensitive range becomes sensitive.
        await Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.conditions SET code = 'F32.A' WHERE id = @id", ("id", hypertension));
        Assert.Equal(["F32.A true", "F41.1 true"], await FlagsAsync());
    }

    // A diagnosis that was recorded before the rule, or by something that skipped the trigger, is marked the next time
    // the policies are applied, as the migration job does on every start; and a code with spaces round it is still
    // caught. The codes added to the rule after step 2b (R45.88, T71 self-harm) are marked the same way.
    [Theory]
    [InlineData(" f41.1 ")]
    [InlineData("R45.88")]
    [InlineData("T71.162A")]
    public async Task Applying_the_policies_again_marks_existing_diagnoses_in_a_sensitive_range(string code)
    {
        var (clinician, patient) = await OnPanelAsync();
        var id = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            """
            SET LOCAL session_replication_role = replica;
            INSERT INTO ehr.conditions (id, patient_id, code, display, clinical_status, sensitive) VALUES (@id, @patient, @code, 'Synthetic', 'active', false)
            """,
            ("id", id), ("patient", patient), ("code", code));
        Assert.Equal(["false"], await AsSuperuserAsync("SELECT sensitive::text FROM ehr.conditions WHERE id = @id", ("id", id)));

        await database.ApplyAsync();

        Assert.Equal(["true"], await AsSuperuserAsync("SELECT sensitive::text FROM ehr.conditions WHERE id = @id", ("id", id)));
        Assert.Equal(["true true"], await AsSuperuserAsync(
            "SELECT relrowsecurity || ' ' || relforcerowsecurity FROM pg_class WHERE oid = 'ehr.conditions'::regclass"));
        // The change is in the history clinicians read, made by nobody signed in.
        Assert.Equal(["1"], await Accounts.QueryAsync(App, clinician,
            "SELECT count(*) FROM ehr.chart_history WHERE row_id = @id AND changed_by_id IS NULL AND (after ->> 'sensitive') = 'true'", ("id", id)));
    }

    // A patient's allergy list is nothing recorded, "No known allergies", or allergies: never the last two at once.
    [Fact]
    public async Task No_known_allergies_is_refused_beside_an_allergy_and_cleared_when_one_is_added()
    {
        var (clinician, patient) = await OnPanelAsync();
        const string none = "INSERT INTO ehr.allergy_intolerances (id, patient_id, code, display, clinical_status, recorded_at) VALUES (@id, @patient, 'no-known-allergy', 'No known allergy', 'active', now())";
        var noneKnown = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician, none, ("id", noneKnown), ("patient", patient));
        Task<List<string>> ListAsync() => AsSuperuserAsync(
            "SELECT display || ' ' || clinical_status FROM ehr.allergy_intolerances WHERE patient_id = @patient ORDER BY recorded_at, display", ("patient", patient));
        Assert.Equal(["No known allergy active"], await ListAsync());

        // A second one, while the first is active, is refused as a unique violation of the index that also holds it,
        // so the app can tell it from the refusals below; and the index holds with the triggers off, as the seed has them.
        var second = await Assert.ThrowsAsync<PostgresException>(() =>
            Accounts.ExecuteAsync(App, clinician, none, ("id", Guid.CreateVersion7()), ("patient", patient)));
        Assert.Equal((PostgresErrorCodes.UniqueViolation, "ix_allergy_intolerances_one_no_known_allergy"), (second.SqlState, second.ConstraintName));
        var bypassed = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "SET LOCAL session_replication_role = replica; " + none, ("id", Guid.CreateVersion7()), ("patient", patient)));
        Assert.Equal((PostgresErrorCodes.UniqueViolation, "ix_allergy_intolerances_one_no_known_allergy"), (bypassed.SqlState, bypassed.ConstraintName));
        Assert.Equal(["No known allergy active"], await ListAsync());

        // Adding an allergy makes it inactive, in the same transaction, by the same clinician, and the history says so.
        var peanut = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician, Statements["allergy_intolerances"].Insert, ("id", peanut), ("patient", patient));
        Assert.Equal(["No known allergy inactive", "Peanut active"], await ListAsync());
        Assert.Equal([$"{clinician} active inactive"], await AsSuperuserAsync(
            "SELECT changed_by_id || ' ' || (before ->> 'clinical_status') || ' ' || (after ->> 'clinical_status') FROM ehr.chart_history WHERE row_id = @id AND before IS NOT NULL",
            ("id", noneKnown)));

        // And it can't be recorded, or made active again, while the allergy is active.
        foreach (var (sql, id) in new[] { (none, Guid.CreateVersion7()), ("UPDATE ehr.allergy_intolerances SET clinical_status = 'active' WHERE id = @id AND @patient = patient_id", noneKnown) })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician, sql, ("id", id), ("patient", patient)));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }

        // Once the allergy is inactive, it can be.
        await Accounts.ExecuteAsync(App, clinician, Statements["allergy_intolerances"].Update, ("id", peanut));
        await Accounts.ExecuteAsync(App, clinician, none, ("id", Guid.CreateVersion7()), ("patient", patient));
        Assert.Equal(["No known allergy inactive", "Peanut inactive", "No known allergy active"], await ListAsync());
    }

    // Recorded at the same moment, "No known allergies" and an allergy can't both stay active, and no two "No known
    // allergies" can: each patient's allergy entries are written one at a time, so whichever comes second sees the first.
    [Fact]
    public async Task No_known_allergies_and_an_allergy_recorded_at_once_never_both_stay_active()
    {
        const string none = "INSERT INTO ehr.allergy_intolerances (id, patient_id, code, display, clinical_status, recorded_at) VALUES (@id, @patient, 'no-known-allergy', 'No known allergy', 'active', now())";
        for (var round = 0; round < 8; round++)
        {
            var (clinician, patient) = await OnPanelAsync();
            await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
            {
                try
                {
                    await Accounts.ExecuteAsync(App, clinician, i % 2 == 0 ? none : Statements["allergy_intolerances"].Insert,
                        ("id", Guid.CreateVersion7()), ("patient", patient));
                }
                catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.CheckViolation or PostgresErrorCodes.UniqueViolation)
                {
                    // "No known allergies" arrived after an allergy, or after another "No known allergies": refused.
                }
            }));

            Assert.Equal(["false false"], await AsSuperuserAsync(
                """
                SELECT (bool_or(code = 'no-known-allergy') AND bool_or(code <> 'no-known-allergy'))::text
                    || ' ' || (count(*) FILTER (WHERE code = 'no-known-allergy') > 1)::text
                FROM ehr.allergy_intolerances WHERE patient_id = @patient AND clinical_status = 'active'
                """,
                ("patient", patient)));
        }
    }

    [Fact]
    public async Task Clinicians_read_the_history_and_nobody_else_does()
    {
        var (clinician, patient) = await OnPanelAsync();
        var id = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician, Statements["conditions"].Insert, ("id", id), ("patient", patient));
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var patientAccount = await Accounts.PatientAccountAsync(database, patient);
        const string read = "SELECT count(*) FROM ehr.chart_history WHERE row_id = @id";

        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal(["1"], await Accounts.QueryAsync(connection, colleague, read, ("id", id)));
            foreach (var refused in new Guid?[] { admin, patientAccount, null, Guid.NewGuid() })
            {
                Assert.Equal(["0"], await Accounts.QueryAsync(connection, refused, read, ("id", id)));
            }
        }
    }

    // The reference codes are loaded with the schema (db/reference), each kind from its source, and a diagnosis is
    // flagged by the same rule the trigger uses.
    [Fact]
    public async Task The_reference_codes_are_loaded_and_flag_sensitive_diagnoses()
    {
        Assert.Equal(["allergen rxnorm true", "allergen unii true", "condition icd-10-cm true", "medication rxnorm true"], await Accounts.QueryAsync(Read, null,
            "SELECT kind || ' ' || system || ' ' || (count(*) > 10) FROM ehr.clinical_codes GROUP BY kind, system ORDER BY kind, system"));
        Assert.Equal(["0"], await Accounts.QueryAsync(Read, null,
            "SELECT count(*) FROM ehr.clinical_codes WHERE sensitive <> (kind = 'condition' AND ehr.icd10cm_is_sensitive(code))"));
        Assert.Equal(["Generalized anxiety disorder true"], await Accounts.QueryAsync(Read, null,
            "SELECT display || ' ' || sensitive FROM ehr.clinical_codes WHERE kind = 'condition' AND code = 'F41.1'"));
        // A code added to the rule later is flagged in the loaded list too: loading the reference data compares the flag.
        Assert.Equal(["Nonsuicidal self-harm true"], await Accounts.QueryAsync(Read, null,
            "SELECT display || ' ' || sensitive FROM ehr.clinical_codes WHERE kind = 'condition' AND code = 'R45.88'"));
    }
}
