using Ehr.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ehr.Migrations;

// Used by the dotnet-ef tool and the migration bundle. Adding and scripting migrations needs no database.
// The bundle (infra/migrate) connects with EHR_MIGRATIONS_CONNECTION, and Npgsql takes the password from
// PGPASSWORD, so it never appears in a connection string or on a command line. What a migration reports with RAISE
// NOTICE is printed in the bundle's output (DatabaseNotices).
public class EhrDbContextFactory : IDesignTimeDbContextFactory<EhrDbContext>
{
    public EhrDbContext CreateDbContext(string[] args) =>
        Create(Environment.GetEnvironmentVariable("EHR_MIGRATIONS_CONNECTION") ?? "Host=localhost;Database=ehr;Username=ehr_owner", Console.Out);

    // The context the bundle migrates with, its notices written to `notices`: the tests use it with a writer of their own.
    public static EhrDbContext Create(string connectionString, TextWriter notices)
    {
        var builder = new DbContextOptionsBuilder<EhrDbContext>();
        EhrDbContext.Configure(builder, connectionString);
        builder.AddInterceptors(new DatabaseNotices(notices));
        return new EhrDbContext(builder.Options);
    }
}
