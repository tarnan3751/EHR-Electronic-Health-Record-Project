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
