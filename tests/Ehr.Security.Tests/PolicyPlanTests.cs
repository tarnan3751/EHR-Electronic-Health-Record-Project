using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// The plans PostgreSQL makes for a clinician through ehr_app. The policies ask who the user is once per statement
// (db/policies/30-rls.sql): a policy that calls ehr.is_clinician() or ehr.current_patient_id() directly shows it in a
// Filter, run for every row, where a scalar subquery shows an InitPlan. And a name search is answered by the name keys'
// indexes under row-level security (PatientConfiguration, PhiReader), which a condition PostgreSQL can't run before the
// policy, such as ILIKE, never is.
public class PolicyPlanTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    [Theory]
    [InlineData("patients")]
    [InlineData("patient_assignments")]
    [InlineData("break_glass_accesses")]
    [InlineData("allergy_intolerances")]
    [InlineData("medication_statements")]
    [InlineData("conditions")]
    [InlineData("encounters")]
    [InlineData("observations")]
    [InlineData("audit_events")]
    [InlineData("clinical_drafts")]
    [InlineData("clinical_notes")]
    [InlineData("note_addenda")]
    [InlineData("visit_summaries")]
    [InlineData("refill_requests")]
    [InlineData("orders")]
    public async Task Policies_ask_who_the_user_is_once_per_statement(string table)
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);

        // Every column the runtime roles may read: all of them, but for audit events' details (10-grants.sql).
        var plan = await PlanAsync(clinician, table == "audit_events" ? "SELECT id, action FROM ehr.audit_events" : $"SELECT * FROM ehr.{table}");

        Assert.Contains("InitPlan", plan);
        Assert.DoesNotContain("ehr.is_", plan);
        Assert.DoesNotContain("ehr.current_", plan);
    }

    // A condition that has to wait for the policy can only be checked row by row: a sequential scan, however costly.
    [Fact]
    public async Task A_name_search_and_a_date_of_birth_are_answered_by_indexes_under_row_level_security()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);

        var byName = await PlanAsync(clinician,
            """
            SELECT id FROM ehr.patients
            WHERE (family_name_key >= lower('Lar') AND family_name_key < ehr.name_key_after(lower('Lar')))
               OR (given_name_key >= lower('Lar') AND given_name_key < ehr.name_key_after(lower('Lar')))
            ORDER BY family_name, given_name, id LIMIT 100
            """);
        var byBirthDate = await PlanAsync(clinician,
            "SELECT id FROM ehr.patients WHERE birth_date = '1971-03-14' ORDER BY family_name, given_name, id LIMIT 100");

        Assert.Contains("ix_patients_family_name_key", byName);
        Assert.Contains("ix_patients_given_name_key", byName);
        Assert.Contains("ix_patients_birth_date", byBirthDate);
        Assert.All([byName, byBirthDate], plan =>
        {
            Assert.DoesNotContain("Seq Scan", plan);
            Assert.Contains("InitPlan", plan);
            Assert.DoesNotContain("ehr.current_", plan);
        });
    }

    // The keys, their indexes, and a comparison with a parameter, as EF Core sends one, all in the C collation, which
    // compares code points. Under the database's own collation, en_US.utf8 in the official image, punctuation and
    // spaces count for little on the first pass, and "obama" would sort inside the range for "o'b".
    [Fact]
    public async Task The_name_keys_compare_in_the_C_collation()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        foreach (var family in new[] { "O'Brien", "Obama" })
        {
            await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
                """
                INSERT INTO ehr.patients (id, mrn, given_name, family_name, birth_date, gender, version, updated_at)
                VALUES (@id, @mrn, 'Ada', @family, '1980-01-01', 'unknown', 1, now())
                """,
                ("id", Guid.CreateVersion7()), ("mrn", Guid.NewGuid().ToString("N")[..12]), ("family", family + suffix));
        }

        var collations = await Accounts.QueryAsync(database.SuperuserConnectionString, null,
            """
            SELECT c.collname FROM pg_attribute a JOIN pg_collation c ON c.oid = a.attcollation
            WHERE a.attrelid = 'ehr.patients'::regclass AND a.attname IN ('family_name_key', 'given_name_key')
            UNION ALL
            SELECT c.collname FROM pg_index i JOIN pg_class x ON x.oid = i.indexrelid JOIN pg_collation c ON c.oid = i.indcollation[0]
            WHERE x.relname IN ('ix_patients_family_name_key', 'ix_patients_given_name_key')
            """);
        var inRange = await Accounts.QueryAsync(database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword), clinician,
            "SELECT family_name FROM ehr.patients WHERE family_name_key >= @from AND family_name_key < @to",
            ("from", "o'b"), ("to", "o'c"));

        Assert.Equal(["C", "C", "C", "C"], collations);
        Assert.Contains("O'Brien" + suffix, inRange);
        Assert.DoesNotContain("Obama" + suffix, inRange);
    }

    // A clinician's own panel is read from their open assignments alone, however long their history (step 13c2's
    // benchmark gives one 60,000 ended assignments): the clinic's home (PhiReader.PanelAsync, and whether more are on it),
    // a search's marks of who is on the panel (PatientAssignments.PanelIdsAsync) and the chart's gate (IsOnPanelAsync), as
    // EF Core writes them, each read no ended assignment, here 100 open among 6,000 ended, the tables counted for the
    // planner.
    [Fact]
    public async Task A_clinicians_panel_is_read_without_their_ended_assignments()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            """
            WITH seeded AS (
                INSERT INTO ehr.patients (id, mrn, given_name, family_name, birth_date, gender, version, updated_at)
                SELECT gen_random_uuid(), substr(md5(random()::text), 1, 12), 'Test', md5(random()::text), '1980-01-01', 'unknown', 1, now()
                FROM generate_series(1, 600)
                RETURNING id
            ),
            numbered AS (SELECT id, row_number() OVER (ORDER BY id) AS n FROM seeded)
            INSERT INTO ehr.patient_assignments (id, patient_id, clinician_id, assigned_at, ended_at)
            SELECT gen_random_uuid(), id, @clinician, now() - interval '1 day', NULL FROM numbered WHERE n <= 100
            UNION ALL
            SELECT gen_random_uuid(), id, @clinician, now() - make_interval(days => 800 + t.round), now() - make_interval(days => 799 + t.round)
            FROM numbered CROSS JOIN generate_series(1, 10) AS t(round)
            """, ("clinician", clinician));
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null, "ANALYZE ehr.patient_assignments; ANALYZE ehr.patients");
        var onPanel = (await Accounts.QueryAsync(database.SuperuserConnectionString, null,
            "SELECT patient_id::text FROM ehr.patient_assignments WHERE clinician_id = @clinician AND ended_at IS NULL LIMIT 1", ("clinician", clinician))).Single();

        const string Panel = "EXISTS (SELECT 1 FROM ehr.patient_assignments AS p0 WHERE p0.patient_id = p.id AND p0.clinician_id = @clinician AND p0.ended_at IS NULL)";
        foreach (var sql in new[]
        {
            $"SELECT p.id FROM ehr.patients AS p WHERE {Panel} ORDER BY p.family_name, p.given_name, p.id LIMIT 100",
            $"SELECT EXISTS (SELECT 1 FROM ehr.patients AS p WHERE {Panel} ORDER BY p.family_name, p.given_name, p.id OFFSET 100)",
            "SELECT p.patient_id FROM ehr.patient_assignments AS p WHERE p.clinician_id = @clinician AND p.ended_at IS NULL",
            $"SELECT EXISTS (SELECT 1 FROM ehr.patient_assignments AS p WHERE p.clinician_id = @clinician AND p.ended_at IS NULL AND p.patient_id = '{onPanel}')",
        })
        {
            await using var connection = new NpgsqlConnection(database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword));
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await using (var set = new NpgsqlCommand("SELECT set_config('app.user_id', @user_id, true)", connection, transaction))
            {
                set.Parameters.AddWithValue("user_id", clinician.ToString());
                await set.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await using var explain = new NpgsqlCommand($"EXPLAIN (ANALYZE, COSTS OFF, TIMING OFF, SUMMARY OFF) {sql}", connection, transaction);
            explain.Parameters.AddWithValue("clinician", clinician);
            var lines = new List<string>();
            await using (var reader = await explain.ExecuteReaderAsync(TestContext.Current.CancellationToken))
            {
                while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                {
                    lines.Add(reader.GetString(0));
                }
            }

            // Each node on the panels' table or one of its indexes, with the lines that describe it, down to the next node.
            var nodes = lines.Select((line, index) => (line, index)).Where(node => node.line.Contains("patient_assignments", StringComparison.Ordinal) && !node.line.TrimStart().StartsWith("SubPlan", StringComparison.Ordinal))
                .Select(node => string.Join('\n', lines.Skip(node.index).TakeWhile((line, offset) => offset == 0 || !line.Contains("->", StringComparison.Ordinal)))).ToList();
            var plan = string.Join('\n', lines);
            Assert.NotEmpty(nodes);
            Assert.All(nodes, node =>
            {
                Assert.DoesNotContain("Rows Removed", node, StringComparison.Ordinal);
                var read = System.Text.RegularExpressions.Regex.Match(node, @"actual rows=([0-9.]+) loops=([0-9]+)");
                Assert.True(read.Success, plan);
                Assert.True(double.Parse(read.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * int.Parse(read.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) <= 100, plan);
            });
        }
    }

    // EXPLAIN, as the user, through ehr_app, in a transaction that sets the user first as UserContextInterceptor does,
    // with sequential scans made to cost the earth, so a plan shows whether an index can answer at all.
    async Task<string> PlanAsync(Guid userId, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var set = new NpgsqlCommand(
            "SELECT set_config('app.user_id', @user_id, true), set_config('enable_seqscan', 'off', true)", connection, transaction))
        {
            set.Parameters.AddWithValue("user_id", userId.ToString());
            await set.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var explain = new NpgsqlCommand($"EXPLAIN (COSTS OFF) {sql}", connection, transaction);
        await using var reader = await explain.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var lines = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join("\n", lines);
    }
}
