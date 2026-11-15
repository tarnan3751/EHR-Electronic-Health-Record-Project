using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// The plans PostgreSQL makes for a clinician through ehr_app. The policies ask who the user is once per statement
// (db/policies/30-rls.sql): a policy that calls ehr.is_clinician() or ehr.current_patient_id() directly shows it in a
// Filter, run for every row, where a scalar subquery shows an InitPlan.
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
    [InlineData("audit_events")]
    public async Task Policies_ask_who_the_user_is_once_per_statement(string table)
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);

        var plan = await PlanAsync(clinician, $"SELECT * FROM ehr.{table}");

        Assert.Contains("InitPlan", plan);
        Assert.DoesNotContain("ehr.is_", plan);
        Assert.DoesNotContain("ehr.current_", plan);
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
