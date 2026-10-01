using DotNet.Testcontainers.Configurations;
using Ehr.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Ehr.Security.Tests;

// A real PostgreSQL 18, set up the way infra/migrate sets up the primary: the initdb bootstrap (ehr_owner and the
// ehr schema), then, as ehr_owner, db/policies/00-roles.sql, the EF Core migrations, and the rest of db/policies.
public sealed class MigratedDatabase : IAsyncLifetime
{
    public const string AppPassword = "test-app";
    public const string ReadPassword = "test-read";
    const string OwnerPassword = "test-owner";

    readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18")
        .WithDatabase("ehr")
        .WithEnvironment("EHR_OWNER_PASSWORD", OwnerPassword)
        .WithResourceMapping(
            new FileInfo(RepositoryRoot.Combine("infra/compose/postgres/primary/initdb/20-ehr-owner.sh")),
            "/docker-entrypoint-initdb.d/",
            fileMode: Unix.FileMode755)
        .Build();

    // The superuser, for inspecting the catalog.
    public string SuperuserConnectionString => container.GetConnectionString();

    public string ConnectionStringFor(string role, string password) =>
        new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Username = role, Password = password }.ConnectionString;

    public async ValueTask InitializeAsync()
    {
        await container.StartAsync();
        await ApplyAsync();
    }

    // Everything infra/migrate does, in the same order. Safe to run more than once, like infra/migrate.
    public async Task ApplyAsync()
    {
        var owner = ConnectionStringFor("ehr_owner", OwnerPassword);

        await ExecuteAsync(owner, await File.ReadAllTextAsync(PolicyFile("00-roles.sql")));
        await ExecuteAsync(owner, $"ALTER ROLE ehr_app PASSWORD '{AppPassword}'; ALTER ROLE ehr_read PASSWORD '{ReadPassword}'");

        var options = new DbContextOptionsBuilder<EhrDbContext>();
        EhrDbContext.Configure(options, owner);
        await using (var context = new EhrDbContext(options.Options))
        {
            await context.Database.MigrateAsync();
        }

        var policies = Directory.GetFiles(RepositoryRoot.Combine("db/policies"), "*.sql")
            .Where(file => Path.GetFileName(file) != "00-roles.sql")
            .Order(StringComparer.Ordinal);
        foreach (var file in policies)
        {
            await ExecuteAsync(owner, await File.ReadAllTextAsync(file));
        }
    }

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() => await container.DisposeAsync();

    static string PolicyFile(string name) => RepositoryRoot.Combine(Path.Combine("db/policies", name));
}
