using System.Collections.Concurrent;
using System.Net.Sockets;
using Ehr.Testing;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Ehr.Web.Tests;

// The web app in memory, configured as each server, against one migrated database. The cloud app reads as ehr_read
// like the real one, but from the primary: there's no replica in these tests. Its WAN link to the on-prem app is an
// in-memory one, which records what crosses it.
public sealed class WebApps : IAsyncLifetime
{
    public MigratedDatabase Database { get; } = new();

    public WebApplicationFactory<Program> OnPrem { get; private set; } = null!;

    public WebApplicationFactory<Program> Cloud { get; private set; } = null!;

    // The cloud app with its link to the on-prem app down.
    public WebApplicationFactory<Program> CloudCutOff { get; private set; } = null!;

    // Each request the cloud app forwarded, as "METHOD /path?query".
    public ConcurrentQueue<string> Forwarded { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await Database.InitializeAsync();
        OnPrem = new App(Site.OnPrem, Database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword));

        // Starting the on-prem app first also creates the Data Protection key, which the cloud app can't create.
        var link = new RecordingLink(OnPrem.Server.CreateHandler(), Forwarded);
        var readOnly = Database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);
        Cloud = new App(Site.Cloud, readOnly, new OnPremApp("https://onprem.test", new HttpMessageInvoker(link)));
        CloudCutOff = new App(Site.Cloud, readOnly, new OnPremApp("https://onprem.test", new HttpMessageInvoker(new CutOffLink())));
    }

    public async ValueTask DisposeAsync()
    {
        await CloudCutOff.DisposeAsync();
        await Cloud.DisposeAsync();
        await OnPrem.DisposeAsync();
        await Database.DisposeAsync();
    }

    public static string NewShortcut() => "." + Guid.NewGuid().ToString("N")[..12];

    public async Task<Guid> InsertAsync(string shortcut, string body, int version = 1)
    {
        var id = Guid.CreateVersion7();
        await using var connection = await OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO ehr.quick_texts (id, shortcut, body, version, updated_at) VALUES (@id, @shortcut, @body, @version, now())",
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("shortcut", shortcut);
        command.Parameters.AddWithValue("body", body);
        command.Parameters.AddWithValue("version", version);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    public async Task<List<(string Shortcut, string Body, int Version)>> RowsAsync(string shortcut)
    {
        await using var connection = await OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "SELECT shortcut, body, version FROM ehr.quick_texts WHERE shortcut = @shortcut", connection);
        command.Parameters.AddWithValue("shortcut", shortcut);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<(string, string, int)>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        }

        return rows;
    }

    // As the superuser, to set up and inspect rows around what the apps do.
    public async Task<NpgsqlConnection> OpenDatabaseAsync()
    {
        var connection = new NpgsqlConnection(Database.SuperuserConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    sealed class App(Site site, string connectionString, OnPremApp? onPrem = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder
                .UseSetting("Ehr:Site", site.ToString())
                .UseSetting("ConnectionStrings:Ehr", connectionString)
                .UseSetting("Ehr:OnPrem:Url", "https://onprem.test");
            if (onPrem is not null)
            {
                builder.ConfigureTestServices(services => services.AddSingleton(onPrem));
            }
        }
    }

    sealed class RecordingLink(HttpMessageHandler onPrem, ConcurrentQueue<string> log) : DelegatingHandler(onPrem)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            log.Enqueue($"{request.Method} {request.RequestUri!.PathAndQuery}");
            return base.SendAsync(request, cancellationToken);
        }
    }

    sealed class CutOffLink : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused));
    }
}
