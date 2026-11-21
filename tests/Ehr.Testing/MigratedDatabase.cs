using System.Security.Cryptography;
using System.Text;
using DotNet.Testcontainers.Configurations;
using Ehr.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Ehr.Testing;

// A real PostgreSQL 18, set up the way infra/migrate sets up the primary: the initdb bootstrap (ehr_owner and the
// ehr schema), then, as ehr_owner, db/policies/00-roles.sql, the EF Core migrations, the rest of db/policies, the
// reference data, and the hash of the lab's key (LabKey: a lab is connected in every test database).
//
// For the tests it runs as Testcontainers starts PostgreSQL, with fsync, synchronous_commit and full_page_writes off:
// a commit returns before its changes reach the disk. That makes the suites fast and changes nothing they check:
// every later query sees a committed transaction the same either way, and no test crashes the server. The render-time
// benchmark times commits, so its database (Durable) has all three on, as the stack's primary does.
public sealed class MigratedDatabase : IAsyncLifetime
{
    public const string AppPassword = "test-app";
    public const string ReadPassword = "test-read";

    // The lab's key (the expansion plan's step 12), as infra/migrate takes it from EHR_LAB_KEY; the database holds its
    // hash. Synthetic, for tests only.
    public const string LabKey = "a-lab-key-for-the-tests-only-not-a-secret";

    // The hash the lab's door is given and finds (db/policies/76-lab.sql): hex SHA-256 of the key.
    public static string LabKeyHash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(LabKey)));
    const string OwnerPassword = "test-owner";

    readonly PostgreSqlContainer container;

    public MigratedDatabase()
        : this(durable: false)
    {
    }

    MigratedDatabase(bool durable)
    {
        var builder = new PostgreSqlBuilder("postgres:18")
            .WithDatabase("ehr")
            .WithEnvironment("EHR_OWNER_PASSWORD", OwnerPassword)
            .WithResourceMapping(
                new FileInfo(RepositoryRoot.Combine("infra/compose/postgres/primary/initdb/20-ehr-owner.sh")),
                "/docker-entrypoint-initdb.d/",
                fileMode: Unix.FileMode755);
        if (durable)
        {
            // In place of Testcontainers' own command, which turns the three off.
            builder = builder.WithCommand(new OverwriteEnumerable<string>(
                ["-c", "fsync=on", "-c", "synchronous_commit=on", "-c", "full_page_writes=on"]));
        }

        container = builder.Build();
    }

    // One whose commits wait for the disk, for the benchmark.
    public static MigratedDatabase Durable() => new(durable: true);

    // The superuser, for inspecting the catalog.
    public string SuperuserConnectionString => container.GetConnectionString();

    // The migration role, which also runs the synthetic seed (bench/Ehr.Seed).
    public string OwnerConnectionString => ConnectionStringFor("ehr_owner", OwnerPassword);

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

        // The reference data (db/reference), then DDInter's, in its own folder with its own license: as infra/migrate does.
        foreach (var file in Directory.GetFiles(RepositoryRoot.Combine("db/reference"), "*.sql").Order(StringComparer.Ordinal)
            .Concat(Directory.GetFiles(RepositoryRoot.Combine("db/reference/ddinter"), "*.sql").Order(StringComparer.Ordinal)))
        {
            await ReferenceData.ApplyAsync(owner, file);
        }

        // The lab's key, last, as infra/migrate stores it: only its hash.
        await ExecuteAsync(owner, $"SELECT ehr.set_lab_key('{LabKeyHash}')");
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
