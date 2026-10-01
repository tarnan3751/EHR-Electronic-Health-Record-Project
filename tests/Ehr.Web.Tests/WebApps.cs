using Ehr.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Ehr.Web.Tests;

// The web app in memory, configured as each server, against one migrated database. The cloud app reads as
// ehr_read like the real one, but from the primary: there's no replica in these tests.
public sealed class WebApps : IAsyncLifetime
{
    public MigratedDatabase Database { get; } = new();

    public WebApplicationFactory<Program> OnPrem { get; private set; } = null!;

    public WebApplicationFactory<Program> Cloud { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Database.InitializeAsync();
        OnPrem = new App(Site.OnPrem, Database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword));
        Cloud = new App(Site.Cloud, Database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword));
    }

    public async ValueTask DisposeAsync()
    {
        await OnPrem.DisposeAsync();
        await Cloud.DisposeAsync();
        await Database.DisposeAsync();
    }

    sealed class App(Site site, string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
            .UseSetting("Ehr:Site", site.ToString())
            .UseSetting("ConnectionStrings:Ehr", connectionString);
    }
}
