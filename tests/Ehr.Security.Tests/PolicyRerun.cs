using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// A policy file runs on every start of the migration job, while pages call what it defines: what it replaces must keep
// its identity, so a call made as it runs is never refused ("cache lookup failed for function", when a file dropped and
// created a function again on each run). RunAsync runs the file five times as the migration role while two connections
// of each runtime role call a function as a page does, a pooled connection and one transaction with the user context,
// over and over until the runs end (at least 50 times each, never more than 1,000), and gives back every error a call
// met. One run is enough for the callers' tests to show the function kept its identity; the others, and the calls, are
// for the concurrency.
static class PolicyRerun
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public static async Task<List<string>> RunAsync(MigratedDatabase database, string policyFile, Guid user, string call, params (string Name, object Value)[] parameters)
    {
        var policy = await File.ReadAllTextAsync(RepositoryRoot.Combine("db/policies/" + policyFile), Cancel);
        var rerunning = true;

        async Task<List<string>> CallAsync(string connectionString)
        {
            var errors = new List<string>();
            for (var calls = 0; calls < 1_000 && (rerunning || calls < 50); calls++)
            {
                try
                {
                    await using var connection = new NpgsqlConnection(connectionString);
                    await connection.OpenAsync(Cancel);
                    await using var transaction = await connection.BeginTransactionAsync(Cancel);
                    await using var command = new NpgsqlCommand("SELECT set_config('app.user_id', @user, true); " + call, connection, transaction);
                    command.Parameters.AddWithValue("user", user.ToString());
                    foreach (var (name, value) in parameters)
                    {
                        command.Parameters.AddWithValue(name, value);
                    }

                    await command.ExecuteNonQueryAsync(Cancel);
                    await transaction.CommitAsync(Cancel);
                }
                catch (NpgsqlException error)
                {
                    errors.Add((error as PostgresException)?.SqlState + " " + error.Message);
                }
            }

            return errors;
        }

        async Task RerunAsync()
        {
            try
            {
                for (var run = 0; run < 5; run++)
                {
                    await MigratedDatabase.ExecuteAsync(database.OwnerConnectionString, policy);
                }
            }
            finally
            {
                rerunning = false;
            }
        }

        var app = database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);
        var read = database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);
        var calls = Enumerable.Range(0, 2).SelectMany(_ => new[] { CallAsync(app), CallAsync(read) }).ToList();
        await Task.WhenAll([RerunAsync(), .. calls]);
        return [.. calls.SelectMany(call => call.Result)];
    }
}
