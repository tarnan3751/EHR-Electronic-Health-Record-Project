using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Ehr.Data.Accounts;
using Ehr.Data.Patients;
using Ehr.Domain.Accounts;
using Ehr.Testing;
using Ehr.Web.Api.Audit;
using Ehr.Web.Api.Desktop;
using Ehr.Web.Api.Dictation;
using Ehr.Web.CloudAudit;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace Ehr.Web.Tests;

// The web app in memory, configured as each server, against one migrated database. The cloud app reads as ehr_read
// like the real one, but from the primary: there's no replica in these tests. Its WAN link to the on-prem app is an
// in-memory one, which records what crosses it. The on-prem app starts with the bootstrap admin settings, as the
// stack does, and its dictation service is a stub in memory (DictationSidecarStub). A lab is connected: the on-prem
// app has its key (step 12). Every app's emails are kept rather than sent.
public sealed class WebApps : IAsyncLifetime
{
    public const string BootstrapAdminEmail = "admin@example.test";

    public const string BootstrapAdminPassword = "the-first-admin-password";

    // Every staff account the tests create has this password.
    public const string StaffPassword = "a-long-enough-password";

    StaffAccount? clinician;

    string? desktopToken;

    public MigratedDatabase Database { get; } = new();

    public WebApplicationFactory<Program> OnPrem { get; private set; } = null!;

    // The on-prem app as it runs outside Development, where a failing page gets the error pages (ErrorPages) rather
    // than the developer exception page. Like the others, it starts when a test first uses it.
    public WebApplicationFactory<Program> OnPremProduction { get; private set; } = null!;

    public WebApplicationFactory<Program> Cloud { get; private set; } = null!;

    // The cloud app with its link to the on-prem app down. It starts knowing so (App's unreachable), as a cloud app is a
    // moment after it starts.
    public WebApplicationFactory<Program> CloudCutOff { get; private set; } = null!;

    // The cloud app linked to an on-prem app that can be made to fail. While PrimaryDown is set, the on-prem app still
    // answers, but with 500 to whatever needs its database, as in the "Primary down" drill (infra/compose/README.md);
    // BrokenOnPremPage answers that one path with 500, as a page with a bug would, while the rest works.
    public WebApplicationFactory<Program> CloudPrimaryDrill { get; private set; } = null!;

    FaultLink faultLink = null!;

    public bool PrimaryDown
    {
        get => faultLink.Down;
        set => faultLink.Down = value;
    }

    public string? BrokenOnPremPage
    {
        get => faultLink.BrokenPath;
        set => faultLink.BrokenPath = value;
    }

    // Each request the cloud app forwarded, as "METHOD /path?query".
    public ConcurrentQueue<string> Forwarded { get; } = new();

    public RecordingEmailSender Emails { get; } = new();

    public DictationSidecarStub Dictation { get; } = new();

    // The sign-in cookies' clock, which a test of a session's end moves on (step 18).
    public ShiftedClock Clock { get; } = new();

    // Rather than 20 seconds, so the test of a service that doesn't answer is quick.
    public static readonly TimeSpan DictationTimeout = TimeSpan.FromSeconds(2);

    // The certificate both apps encrypt the Data Protection keys with, as PEM files, the way the stack mounts it.
    public string KeyCertificatePath { get; } = Path.Combine(Path.GetTempPath(), $"ehr-keys-{Guid.NewGuid():N}.pem");

    public string KeyCertificateKeyPath => KeyCertificatePath[..^4] + "-key.pem";

    // The secret both apps share for shipping the cloud app's audit events.
    public const string CloudAuditKey = "a-cloud-audit-key-for-the-tests-only";
    // The key enrollment codes are hashed with: both apps hold the same one.
    public const string EnrollmentKey = "tests-only-enrollment-code-key-0123456789";

    // Each cloud app keeps its audit queue in its own folder under this one.
    public string CloudAuditRoot { get; } = Path.Combine(Path.GetTempPath(), $"ehr-cloud-audit-{Guid.NewGuid():N}");

    public async ValueTask InitializeAsync()
    {
        using (var key = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=EHR Data Protection", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            await File.WriteAllTextAsync(KeyCertificatePath, certificate.ExportCertificatePem());
            await File.WriteAllTextAsync(KeyCertificateKeyPath, key.ExportPkcs8PrivateKeyPem());
        }

        await Database.InitializeAsync();
        OnPrem = new App(Site.OnPrem, Database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword), Emails, this, bootstrapAdmin: true,
            dictation: new DictationSidecar(DictationSidecarStub.Url, DictationSidecarStub.ApiKey, new HttpMessageInvoker(Dictation), DictationTimeout));
        OnPremProduction = new App(Site.OnPrem, Database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword), Emails, this, environment: Environments.Production);

        // Starting the on-prem app first also creates the Data Protection key, which the cloud app can't create.
        var link = new RecordingLink(OnPrem.Server.CreateHandler(), Forwarded);
        var readOnly = Database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);
        Cloud = new App(Site.Cloud, readOnly, Emails, this, onPrem: new OnPremApp("https://onprem.test", new HttpMessageInvoker(link)));
        CloudCutOff = new App(Site.Cloud, readOnly, Emails, this, onPrem: new OnPremApp("https://onprem.test", new HttpMessageInvoker(new CutOffLink())),
            unreachable: true);
        faultLink = new FaultLink(OnPrem.Server.CreateHandler());
        CloudPrimaryDrill = new App(Site.Cloud, readOnly, Emails, this, onPrem: new OnPremApp("https://onprem.test", new HttpMessageInvoker(faultLink)));
    }

    public async ValueTask DisposeAsync()
    {
        // Whatever got as far as starting.
        if (CloudPrimaryDrill is not null)
        {
            await CloudPrimaryDrill.DisposeAsync();
        }

        if (CloudCutOff is not null)
        {
            await CloudCutOff.DisposeAsync();
        }

        if (Cloud is not null)
        {
            await Cloud.DisposeAsync();
        }

        if (OnPremProduction is not null)
        {
            await OnPremProduction.DisposeAsync();
        }

        if (OnPrem is not null)
        {
            await OnPrem.DisposeAsync();
        }

        await Database.DisposeAsync();
        File.Delete(KeyCertificatePath);
        File.Delete(KeyCertificateKeyPath);
        if (Directory.Exists(CloudAuditRoot))
        {
            Directory.Delete(CloudAuditRoot, recursive: true);
        }
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

    // A staff account that has set its password but not enrolled an authenticator yet: invited, with its setup done.
    public async Task<StaffAccount> CreateStaffWithoutAuthenticatorAsync(string displayName, params string[] roles)
    {
        await using var scope = OnPrem.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        var email = NewEmail();
        var user = new StaffUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = displayName, Roles = roles };
        var created = await users.CreateAsync(user, StaffPassword);
        Assert.True(created.Succeeded, string.Join(" ", created.Errors.Select(e => e.Description)));
        return new(user.Id, email, StaffPassword, AuthenticatorKey: "");
    }

    // A confirmed portal account for the patient, created through the same Identity services the app uses.
    public async Task<(string Email, string Password)> CreatePatientAccountAsync(Guid patientId, bool confirmed = true)
    {
        await using var scope = OnPrem.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PatientUser>>();
        var email = NewEmail();
        const string password = "a-patient-portal-password";
        var user = new PatientUser { UserName = email, Email = email, EmailConfirmed = confirmed, PatientId = patientId };
        var created = await users.CreateAsync(user, password);
        Assert.True(created.Succeeded, string.Join(" ", created.Errors.Select(e => e.Description)));
        return (email, password);
    }

    // What a password change does to the account: every session it had is over.
    public async Task UpdateSecurityStampAsync(StaffAccount account)
    {
        await using var scope = OnPrem.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        var updated = await users.UpdateSecurityStampAsync((await users.FindByIdAsync(account.Id.ToString()))!);
        Assert.True(updated.Succeeded);
    }

    // The desktop session token of the fixture's clinician, from the sign-in API, created once per fixture.
    public async Task<string> DesktopTokenAsync() => desktopToken ??= await DesktopSignInAsync(OnPrem, await ClinicianAsync());

    // Signs the account in as the desktop app would, with its password and a code from its authenticator key.
    public async Task<string> DesktopSignInAsync(WebApplicationFactory<Program> app, StaffAccount account)
    {
        using var response = await DesktopSignInResponseAsync(app, account, Totp.Code(account.AuthenticatorKey));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var signedIn = await response.Content.ReadFromJsonAsync<SignInResponse>(JsonSerializerOptions.Web, TestContext.Current.CancellationToken);
        return signedIn!.Token;
    }

    public Task<HttpResponseMessage> DesktopSignInResponseAsync(WebApplicationFactory<Program> app, StaffAccount account, string code) =>
        Browser.CreateClient(app).PostAsJsonAsync("/api/desktop/sign-in",
            new SignInRequest(account.Email, account.Password, code, "test desktop"), JsonSerializerOptions.Web, TestContext.Current.CancellationToken);

    // A patient row, inserted as the superuser the way a seed would: row-level security doesn't apply, and the audit
    // trigger records no actor. Born on 1980-01-01 unless the test says otherwise; the MRN is MrnOf the ID.
    public async Task<Guid> InsertPatientAsync(string givenName, string familyName, DateOnly? birthDate = null)
    {
        var id = Guid.CreateVersion7();
        await using var connection = await OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ehr.patients (id, mrn, given_name, family_name, birth_date, gender, version, updated_at)
            VALUES (@id, @mrn, @given, @family, @born, 'unknown', 1, now())
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("mrn", MrnOf(id));
        command.Parameters.AddWithValue("given", givenName);
        command.Parameters.AddWithValue("family", familyName);
        command.Parameters.AddWithValue("born", birthDate ?? new DateOnly(1980, 1, 1));
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    // The MRN InsertPatientAsync gives a patient: the random end of the ID, since the start of a version 7 GUID is the
    // time, the same for two made in one millisecond.
    public static string MrnOf(Guid patientId) => patientId.ToString("N")[^12..];

    // The rest of a patient's chart, inserted as the superuser the way a seed would: nothing at runtime writes it yet.
    // An allergy's recorded date is fixed, so a page can be checked for it.
    public Task InsertAllergyAsync(
        Guid patientId, string display, string status = AllergyClinicalStatus.Active, string? code = null, string? criticality = null,
        string? reaction = null) =>
        InsertChartRowAsync(
            """
            INSERT INTO ehr.allergy_intolerances (id, patient_id, code, display, clinical_status, criticality, reaction, recorded_at)
            VALUES (@id, @patient, @code, @display, @status, @criticality, @reaction, '2025-03-04T10:00:00Z')
            """,
            ("patient", patientId), ("code", code), ("display", display), ("status", status), ("criticality", criticality), ("reaction", reaction));

    public Task InsertMedicationAsync(Guid patientId, string display, string status = MedicationStatementStatus.Active, string? dosage = null) =>
        InsertChartRowAsync(
            "INSERT INTO ehr.medication_statements (id, patient_id, display, dosage, status) VALUES (@id, @patient, @display, @dosage, @status)",
            ("patient", patientId), ("display", display), ("dosage", dosage), ("status", status));

    public Task InsertConditionAsync(
        Guid patientId, string display, string status = ConditionClinicalStatus.Active, string? code = null, DateOnly? onset = null) =>
        InsertChartRowAsync(
            "INSERT INTO ehr.conditions (id, patient_id, code, display, clinical_status, onset_date) VALUES (@id, @patient, @code, @display, @status, @onset)",
            ("patient", patientId), ("code", code), ("display", display), ("status", status), ("onset", onset));

    // A visit, with the vital signs taken at it as observations (step 9), each with the name, unit and reference
    // range db/reference gives its code; its blood pressure one reading (step 9c).
    public Task InsertVisitAsync(
        Guid patientId, DateTimeOffset start, string status = EncounterStatus.Finished, string? reason = null, short? systolic = null,
        short? diastolic = null, short? heartRate = null) =>
        InsertChartRowAsync(
            """
            WITH visit AS (
                INSERT INTO ehr.encounters (id, patient_id, status, class_code, reason, period_start)
                VALUES (@id, @patient, @status, 'AMB', @reason, @start)
                RETURNING id, patient_id, period_start)
            INSERT INTO ehr.observations (id, patient_id, encounter_id, reading_id, category, code, display, status, value, unit, reference_low, reference_high, effective_at, issued_at)
            SELECT gen_random_uuid(), visit.patient_id, visit.id, CASE WHEN v.code IN ('8480-6', '8462-4') THEN @reading END, 'vital-signs', v.code, c.display, 'final', v.value, c.unit,
                c.reference_low, c.reference_high, visit.period_start, visit.period_start
            FROM visit
            CROSS JOIN (VALUES ('8480-6', @systolic::numeric), ('8462-4', @diastolic::numeric), ('8867-4', @heart_rate::numeric)) AS v(code, value)
            JOIN ehr.observation_codes c ON c.code = v.code
            WHERE v.value IS NOT NULL
            """,
            ("patient", patientId), ("status", status), ("reason", reason), ("start", start.ToUniversalTime()), ("systolic", systolic),
            ("diastolic", diastolic), ("heart_rate", heartRate), ("reading", Guid.CreateVersion7()));

    async Task InsertChartRowAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
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

    // Ships whatever the connected cloud app has queued now, rather than waiting for its next try.
    public async Task ShipCloudAuditAsync() =>
        Assert.True(await Cloud.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(TestContext.Current.CancellationToken));

    // A test result for the patient, inserted as the superuser the way the lab interface will. Released (issued) unless
    // issuedAt is null.
    public async Task<Guid> InsertResultAsync(
        Guid patientId, string display, string? code, decimal? value, string? unit, decimal? low, decimal? high,
        DateTimeOffset effectiveAt, DateTimeOffset? issuedAt, string status = "final", string? note = null)
    {
        var id = Guid.CreateVersion7();
        await using var connection = await OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ehr.observations (id, patient_id, code, display, status, value, unit, reference_low, reference_high, effective_at, issued_at, note)
            VALUES (@id, @patient, @code, @display, @status, @value, @unit, @low, @high, @effective, @issued, @note)
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("patient", patientId);
        command.Parameters.AddWithValue("code", NpgsqlTypes.NpgsqlDbType.Varchar, (object?)code ?? DBNull.Value);
        command.Parameters.AddWithValue("display", display);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("value", NpgsqlTypes.NpgsqlDbType.Numeric, (object?)value ?? DBNull.Value);
        command.Parameters.AddWithValue("unit", NpgsqlTypes.NpgsqlDbType.Varchar, (object?)unit ?? DBNull.Value);
        command.Parameters.AddWithValue("low", NpgsqlTypes.NpgsqlDbType.Numeric, (object?)low ?? DBNull.Value);
        command.Parameters.AddWithValue("high", NpgsqlTypes.NpgsqlDbType.Numeric, (object?)high ?? DBNull.Value);
        command.Parameters.AddWithValue("effective", effectiveAt);
        command.Parameters.AddWithValue("issued", NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)issuedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("note", NpgsqlTypes.NpgsqlDbType.Varchar, (object?)note ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    // How many audit events match, read as the superuser.
    // Events an anonymous request recorded: the patient, and no actor.
    public async Task<long> AuditEventsWithoutActorAsync(string action, Guid patientId)
    {
        await using var connection = await OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM ehr.audit_events WHERE action = @action AND patient_id = @patient AND actor_id IS NULL", connection);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("patient", patientId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async Task<long> AuditEventsAsync(string action, Guid? actorId = null, Guid? patientId = null, string? source = null, string? table = null)
    {
        await using var connection = await OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM ehr.audit_events
            WHERE action = @action AND (@actor IS NULL OR actor_id = @actor) AND (@patient IS NULL OR patient_id = @patient)
              AND (@source IS NULL OR source = @source) AND (@table IS NULL OR table_name = @table)
            """,
            connection);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("source", NpgsqlTypes.NpgsqlDbType.Text, (object?)source ?? DBNull.Value);
        command.Parameters.AddWithValue("table", NpgsqlTypes.NpgsqlDbType.Text, (object?)table ?? DBNull.Value);
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

    sealed class App(
        Site site, string connectionString, RecordingEmailSender emails, WebApps apps, OnPremApp? onPrem = null, bool bootstrapAdmin = false,
        string? environment = null, DictationSidecar? dictation = null, bool unreachable = false)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Development otherwise, as WebApplicationFactory runs it.
            if (environment is not null)
            {
                builder.UseEnvironment(environment);
            }

            builder
                .UseSetting("Ehr:Site", site.ToString())
                .UseSetting("ConnectionStrings:Ehr", connectionString)
                .UseSetting("Ehr:OnPrem:Url", "https://onprem.test")
                .UseSetting("Ehr:DataProtection:Certificate", apps.KeyCertificatePath)
                .UseSetting("Ehr:DataProtection:CertificateKey", apps.KeyCertificateKeyPath)
                .UseSetting("Ehr:CloudAudit:Key", CloudAuditKey)
                // Required outside Development (step 6c); the tests' own address, so the links in emails can be followed.
                .UseSetting("Ehr:Links:ClinicalBaseUrl", Browser.Base.ToString())
                .UseSetting("Ehr:Links:PortalBaseUrl", Browser.Base.ToString())
                .UseSetting("Ehr:Enrollment:Key", EnrollmentKey)
                // Required outside Development (step 10d). UTC here, as every page read before the step; the tests of
                // the clinic's clock set a zone of their own.
                .UseSetting("Ehr:Clinic:TimeZone", "UTC")
                // Listening to a visit, off unless switched on: on here, for the tests of it (AmbientSwitchTests turn it off).
                .UseSetting(Ehr.Web.Ambient.AmbientListening.Setting, "true")
                .UseSetting("Ehr:CloudAudit:Directory", Path.Combine(apps.CloudAuditRoot, Guid.NewGuid().ToString("N")))
                // Every test client has the same address, so the limits are out of reach (RateLimitTests lowers them).
                .UseSetting("RateLimits:PermitLimit", "1000000")
                .UseSetting("RateLimits:EmailsPerAddressPerHour", "1000000")
                .UseSetting("RateLimits:LabRequestsPerMinute", "1000000")
                .UseSetting("RateLimits:LabKeylessRequestsPerMinute", "1000000");
            // A lab is connected (step 12): the on-prem app has its key, and the database the key's hash
            // (MigratedDatabase). As in the stack, the cloud app isn't given it.
            if (site == Site.OnPrem)
            {
                builder.UseSetting(Ehr.Web.Api.Lab.LabApiKey.Setting, MigratedDatabase.LabKey);
            }

            if (bootstrapAdmin)
            {
                builder
                    .UseSetting("Ehr:Bootstrap:AdminEmail", BootstrapAdminEmail)
                    .UseSetting("Ehr:Bootstrap:AdminPassword", BootstrapAdminPassword);
            }

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IEmailSender>(emails);
                foreach (var scheme in new[] { AccountSchemes.Staff, AccountSchemes.Patient, AccountSchemes.StaffPending })
                {
                    services.PostConfigure<CookieAuthenticationOptions>(scheme, options => options.TimeProvider = apps.Clock);
                }

                if (onPrem is not null)
                {
                    services.AddSingleton(onPrem);
                }

                if (dictation is not null)
                {
                    services.AddSingleton(dictation);
                }

                // A cloud app learns that the on-prem app can't be reached at its audit shipper's first check-in, on a
                // thread of its own once the app has started; on a busy machine a test's first request could come
                // before it and find the clinic reachable. The app with its link down starts as it is a moment later.
                if (unreachable)
                {
                    var status = new OnPremStatus();
                    status.Report(reachable: false);
                    services.AddSingleton(status);
                }
            });
        }
    }

    // Records what the cloud app forwards. The audit shipper's batches cross the same link every couple of seconds
    // but aren't forwarded requests, so they're left out.
    sealed class RecordingLink(HttpMessageHandler onPrem, ConcurrentQueue<string> log) : DelegatingHandler(onPrem)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath != CloudAuditApi.Path)
            {
                log.Enqueue($"{request.Method} {request.RequestUri.PathAndQuery}");
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    sealed class CutOffLink : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused));
    }

    sealed class FaultLink(HttpMessageHandler onPrem) : DelegatingHandler(onPrem)
    {
        volatile bool down;

        volatile string? brokenPath;

        public bool Down
        {
            get => down;
            set => down = value;
        }

        public string? BrokenPath
        {
            get => brokenPath;
            set => brokenPath = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            // With its primary down the on-prem app still answers what doesn't need the database, such as a request to
            // the audit endpoint without the key, which it refuses before reading anything.
            var needsDatabase = path != CloudAuditApi.Path || request.Headers.Contains(CloudAuditApi.KeyHeader);
            return (down && needsDatabase) || path == brokenPath
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))
                : base.SendAsync(request, cancellationToken);
        }
    }
}

// A staff account the tests can sign in as: its password and the key its authenticator app would hold.
public sealed record StaffAccount(Guid Id, string Email, string Password, string AuthenticatorKey);
