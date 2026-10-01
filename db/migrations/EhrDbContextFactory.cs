using Ehr.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ehr.Migrations;

// Used by the dotnet-ef tool and the migration bundle. Adding and scripting migrations needs no database.
// The bundle (infra/migrate) connects with EHR_MIGRATIONS_CONNECTION, and Npgsql takes the password from
// PGPASSWORD, so it never appears in a connection string or on a command line.
public class EhrDbContextFactory : IDesignTimeDbContextFactory<EhrDbContext>
{
    public EhrDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("EHR_MIGRATIONS_CONNECTION")
            ?? "Host=localhost;Database=ehr;Username=ehr_owner";

        var builder = new DbContextOptionsBuilder<EhrDbContext>();
        EhrDbContext.Configure(builder, connectionString);
        return new EhrDbContext(builder.Options);
    }
}
