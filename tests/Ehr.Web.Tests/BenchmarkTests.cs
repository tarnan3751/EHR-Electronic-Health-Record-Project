using System.Net;
using Ehr.Bench;
using Ehr.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Ehr.Web.Tests;

// The benchmark's app (bench/Ehr.Bench) against a database whose key ring it didn't make and can't read: never a new
// Data Protection key there, which would sit unencrypted in a key ring shared by both of the stack's apps. And --stack
// refuses before it starts without the certificate.
public class BenchmarkTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

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
