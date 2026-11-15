using System.Net;
using Ehr.Bench;
using Ehr.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Ehr.Web.Tests;

// The benchmark's app (bench/Ehr.Bench) against a database whose key ring it didn't make and can't read: never a new
// Data Protection key there, which would sit unencrypted in a key ring shared by both of the stack's apps. And --stack
// refuses before it starts without the certificate. Its own database's commits wait for the disk, and it can tell a
// database whose commits don't.
public class BenchmarkTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Its_own_database_waits_for_the_disk_at_every_commit()
    {
        await using var database = MigratedDatabase.Durable();
        await database.InitializeAsync();
        await using var connection = new NpgsqlConnection(database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword));
        await connection.OpenAsync(Cancel);

        var durability = await RenderBenchmark.ReadDurabilityAsync(connection);

        Assert.Equal(new RenderBenchmark.Durability(Fsync: "on", SynchronousCommit: "on", FullPageWrites: "on"), durability);
        Assert.True(durability.IsOn);
    }

    // The tests' own database, as Testcontainers starts PostgreSQL: the benchmark would mark its table and refuse
    // --assert.
    [Fact]
    public async Task It_tells_a_database_whose_commits_dont_wait_for_the_disk()
    {
        await using var connection = new NpgsqlConnection(apps.Database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword));
        await connection.OpenAsync(Cancel);

        var durability = await RenderBenchmark.ReadDurabilityAsync(connection);

        Assert.Equal(new RenderBenchmark.Durability(Fsync: "off", SynchronousCommit: "off", FullPageWrites: "off"), durability);
        Assert.False(durability.IsOn);
    }

    [Fact]
    public void Against_the_stack_without_the_certificate_it_refuses_before_starting()
    {
        var without = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Ehr"] = "Host=127.0.0.1;Database=ehr",
        }).Build();
        var with = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Ehr"] = "Host=127.0.0.1;Database=ehr",
            ["Ehr:DataProtection:Certificate"] = "certs/example.com.pem",
        }).Build();

        Assert.Contains("Ehr:DataProtection:Certificate", RenderBenchmark.StackRefusal(without));
        Assert.Null(RenderBenchmark.StackRefusal(with));
    }

    // The web tests' on-prem app made this database's key, encrypted with a certificate the benchmark's app hasn't got:
    // the case in which an app writes a new, unencrypted key as it starts.
    [Fact]
    public async Task Its_app_adds_no_key_to_a_key_ring_it_cannot_read()
    {
        using (var onPrem = Browser.CreateClient(apps.OnPrem))
        {
            Assert.Equal(HttpStatusCode.OK, (await onPrem.GetAsync("/sign-in", Cancel)).StatusCode);
        }

        var before = await KeysAsync();

        await using (var app = new RenderBenchmark.BenchApp(apps.Database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword)))
        {
            using var client = app.CreateClient(new() { BaseAddress = Browser.Base });
            using var signIn = await client.GetAsync("/sign-in", Cancel);
            Assert.Contains("__RequestVerificationToken", await Browser.ReadAsync(signIn));
        }

        Assert.NotEmpty(before);
        Assert.Equal(before, await KeysAsync());
    }

    async Task<List<string>> KeysAsync()
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT friendly_name FROM ehr.data_protection_keys ORDER BY id", connection);
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        var keys = new List<string>();
        while (await reader.ReadAsync(Cancel))
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }
}
