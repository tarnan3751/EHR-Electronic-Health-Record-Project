using Ehr.Migrations;
using Ehr.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ehr.Data.Tests;

// A migration that changes patient data says what it changed with RAISE NOTICE, and the migration job's output must
// show it. The bundle builds its context with EhrDbContextFactory, whose notices go to standard output; Npgsql alone
// would log them only at the Debug level, which the bundle doesn't print. Here the same context, with a writer of the
// test's own in place of the console.
public class MigrationNoticesTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    [Fact]
    public async Task The_bundles_context_writes_each_notice_the_database_raises()
    {
        var output = new StringWriter();
        await using (var context = EhrDbContextFactory.Create(database.OwnerConnectionString, output))
        {
            await context.Database.ExecuteSqlRawAsync(
                "DO $$ BEGIN RAISE NOTICE 'Synthetic: % rows changed', 3; RAISE NOTICE 'Synthetic: and a second line'; END $$",
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(["Synthetic: 3 rows changed", "Synthetic: and a second line"], output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }
}
