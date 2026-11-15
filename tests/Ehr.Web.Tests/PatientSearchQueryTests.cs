using System.Data.Common;
using System.Text.RegularExpressions;
using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Domain.Audit;
using Ehr.Domain.Phi;
using Ehr.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Ehr.Web.Tests;

// The SQL the PHI reader's name search sends (PhiReader.SearchPatientsAsync). What was typed goes to the database as
// it is, lower-cased there by the lower() that keeps the keys (NameKeys), and the range's end is worked out there too.
// In the C collation the range is already exact, so no search result can tell whether the prefix check after it is
// there; the SQL can: the range, which the keys' indexes answer, and the prefix check, which decides.
public class PatientSearchQueryTests(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_name_search_is_lowered_in_the_database_narrowed_by_a_range_and_decided_by_a_prefix_check()
    {
        var cancel = TestContext.Current.CancellationToken;
        var clinician = await apps.CreateStaffAsync("Dr. Query", StaffRoles.Clinician);
        var user = new UserContext { UserId = clinician.Id };
        var commands = new CapturedCommands();
        var options = new DbContextOptionsBuilder<EhrDbContext>();
        EhrDbContext.Configure(options, apps.Database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword));
        options.AddInterceptors(new UserContextInterceptor(user), commands);
        await using var db = new EhrDbContext(options.Options);
        var reader = new PhiReader(db, new AuditRecorder(db, user, new AuditSource(AuditSources.OnPrem)), user);
        const string typed = @"İNCE_%\x";

        await using (var transaction = await db.Database.BeginTransactionAsync(cancel))
        {
            await reader.SearchPatientsAsync(typed, birthDate: null, mrn: null, 20, cancel);
            await transaction.RollbackAsync(cancel);
        }

        var search = Assert.Single(commands.Executed, command => command.Text.Contains("FROM ehr.patients", StringComparison.Ordinal)
            && command.Text.Contains("LIMIT", StringComparison.Ordinal));
        var name = Regex.Match(search.Text, @"family_name_key >= lower\((@\w+)\)");
        Assert.True(name.Success, $"The range doesn't start at the lowered name:\n{search.Text}");
        var parameter = name.Groups[1].Value;
        // Sent as it was typed: lowered by the database, nothing escaped, nothing changed by .NET.
        Assert.Equal(typed, search.Parameters[parameter]);
        foreach (var key in new[] { "family_name_key", "given_name_key" })
        {
            Assert.Contains($"p.{key} >= lower({parameter})", search.Text, StringComparison.Ordinal);
            Assert.Contains($"p.{key} < ehr.name_key_after(lower({parameter}))", search.Text, StringComparison.Ordinal);
            Assert.Contains($"left(p.{key}, length(lower({parameter}))) = lower({parameter})", search.Text, StringComparison.Ordinal);
        }
    }

    // Every command EF Core sent, with its parameters' values.
    sealed class CapturedCommands : DbCommandInterceptor
    {
        public List<(string Text, Dictionary<string, object?> Parameters)> Executed { get; } = [];

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            Executed.Add((command.CommandText, command.Parameters.Cast<DbParameter>().ToDictionary(p => "@" + p.ParameterName.TrimStart('@'), p => (object?)p.Value)));
            return ValueTask.FromResult(result);
        }
    }
}
