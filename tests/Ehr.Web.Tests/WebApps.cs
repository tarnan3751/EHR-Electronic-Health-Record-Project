using System.Collections.Concurrent;
using System.Net.Sockets;
using Ehr.Data.Accounts;
using Ehr.Domain.Accounts;
using Ehr.Testing;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Ehr.Web.Tests;

// The web app in memory, configured as each server, against one migrated database. The cloud app reads as ehr_read
// like the real one, but from the primary: there's no replica in these tests. Its WAN link to the on-prem app is an
// in-memory one, which records what crosses it. The on-prem app starts with the bootstrap admin settings, as the
// stack does, and every app's emails are kept rather than sent.
public sealed class WebApps : IAsyncLifetime
{
    public const string BootstrapAdminEmail = "admin@example.test";

    public const string BootstrapAdminPassword = "the-first-admin-password";

    // Every staff account the tests create has this password.
    public const string StaffPassword = "a-long-enough-password";

    StaffAccount? clinician;

    public MigratedDatabase Database { get; } = new();

    public WebApplicationFactory<Program> OnPrem { get; private set; } = null!;

    public WebApplicationFactory<Program> Cloud { get; private set; } = null!;

    // The cloud app with its link to the on-prem app down.
    public WebApplicationFactory<Program> CloudCutOff { get; private set; } = null!;

    // Each request the cloud app forwarded, as "METHOD /path?query".
    public ConcurrentQueue<string> Forwarded { get; } = new();

    public RecordingEmailSender Emails { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await Database.InitializeAsync();
        OnPrem = new App(Site.OnPrem, Database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword), Emails, bootstrapAdmin: true);

        // Starting the on-prem app first also creates the Data Protection key, which the cloud app can't create.
        var link = new RecordingLink(OnPrem.Server.CreateHandler(), Forwarded);
        var readOnly = Database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);
        Cloud = new App(Site.Cloud, readOnly, Emails, onPrem: new OnPremApp("https://onprem.test", new HttpMessageInvoker(link)));
        CloudCutOff = new App(Site.Cloud, readOnly, Emails, onPrem: new OnPremApp("https://onprem.test", new HttpMessageInvoker(new CutOffLink())));
    }

    public async ValueTask DisposeAsync()
    {
        // Whatever got as far as starting.
        if (CloudCutOff is not null)
        {
            await CloudCutOff.DisposeAsync();
        }

        if (Cloud is not null)
        {
            await Cloud.DisposeAsync();
        }

        if (OnPrem is not null)
        {
            await OnPrem.DisposeAsync();
        }

        await Database.DisposeAsync();
    }

    public static string NewShortcut() => "." + Guid.NewGuid().ToString("N")[..12];

    public static string NewEmail() => $"{Guid.NewGuid():N}@example.test";

    // The clinician most tests sign in as, created once per fixture.
    public async Task<StaffAccount> ClinicianAsync() => clinician ??= await CreateStaffAsync("Dr. Test", StaffRoles.Clinician);

    // A staff account with a password and an enrolled authenticator, the way the app leaves one after its first
    // sign-in, created through the same Identity services the app uses.
    public async Task<StaffAccount> CreateStaffAsync(string displayName, params string[] roles)
    {
        await using var scope = OnPrem.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        var email = NewEmail();
        var user = new StaffUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = displayName, Roles = roles };
        var created = await users.CreateAsync(user, StaffPassword);
        Assert.True(created.Succeeded, string.Join(" ", created.Errors.Select(e => e.Description)));
        await users.ResetAuthenticatorKeyAsync(user);
        var key = (await users.GetAuthenticatorKeyAsync(user))!;
        await users.SetTwoFactorEnabledAsync(user, true);
        return new(user.Id, email, StaffPassword, key);
    }

    // A patient row, inserted as the superuser the way a seed would: row-level security doesn't apply, and the audit
    // trigger records no actor.
    public async Task<Guid> InsertPatientAsync(string givenName, string familyName)
    {
        var id = Guid.CreateVersion7();
        await using var connection = await OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ehr.patients (id, mrn, given_name, family_name, birth_date, gender, version, updated_at)
            VALUES (@id, @mrn, @given, @family, '1980-01-01', 'unknown', 1, now())
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("mrn", id.ToString("N")[..12]);
        command.Parameters.AddWithValue("given", givenName);
        command.Parameters.AddWithValue("family", familyName);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    // The patient on the clinician's panel, inserted as the superuser the way a seed would, so the chart opens without
    // asking for a reason (BreakGlass).
    public async Task AssignAsync(StaffAccount clinician, Guid patientId)
    {
        await using var connection = await OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO ehr.patient_assignments (id, patient_id, clinician_id, assigned_at) VALUES (@id, @patient, @clinician, now())",
            connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("patient", patientId);
        command.Parameters.AddWithValue("clinician", clinician.Id);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

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

    // How many audit events match, read as the superuser.
    public async Task<long> AuditEventsAsync(string action, Guid? actorId = null, Guid? patientId = null)
    {
        await using var connection = await OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM ehr.audit_events
            WHERE action = @action AND (@actor IS NULL OR actor_id = @actor) AND (@patient IS NULL OR patient_id = @patient)
            """,
            connection);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("actor", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)actorId ?? DBNull.Value);
        command.Parameters.AddWithValue("patient", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)patientId ?? DBNull.Value);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    // The reasons recorded with the matching events, read as the superuser.
    public async Task<List<string>> ReasonsAsync(string action, Guid actorId, Guid patientId)
    {
        await using var connection = await OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "SELECT reason FROM ehr.audit_events WHERE action = @action AND actor_id = @actor AND patient_id = @patient ORDER BY occurred_at",
            connection);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("actor", actorId);
        command.Parameters.AddWithValue("patient", patientId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var reasons = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            reasons.Add(reader.GetString(0));
        }

        return reasons;
    }

    // As the superuser, to set up and inspect rows around what the apps do.
    public async Task<NpgsqlConnection> OpenDatabaseAsync()
    {
        var connection = new NpgsqlConnection(Database.SuperuserConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    sealed class App(Site site, string connectionString, RecordingEmailSender emails, OnPremApp? onPrem = null, bool bootstrapAdmin = false)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder
                .UseSetting("Ehr:Site", site.ToString())
                .UseSetting("ConnectionStrings:Ehr", connectionString)
                .UseSetting("Ehr:OnPrem:Url", "https://onprem.test");
            if (bootstrapAdmin)
            {
                builder
                    .UseSetting("Ehr:Bootstrap:AdminEmail", BootstrapAdminEmail)
                    .UseSetting("Ehr:Bootstrap:AdminPassword", BootstrapAdminPassword);
            }

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IEmailSender>(emails);
                if (onPrem is not null)
                {
                    services.AddSingleton(onPrem);
                }
            });
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

// A staff account the tests can sign in as: its password and the key its authenticator app would hold.
public sealed record StaffAccount(Guid Id, string Email, string Password, string AuthenticatorKey);
