using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Domain.Patients;
using Ehr.Seed;
using Ehr.Testing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Ehr.Bench;

// Server render time for the patient search, the panel and the chart in a clinic of 50,000 synthetic patients (NFR-01,
// NFR-04; AGENTS.md's gate, which --assert checks, is in Gate). The on-prem app runs in memory, so a request's time is
// the server's own: authentication, the page's queries and the reads it records, and rendering, with no network in it.
// Each kind of request is made 100 times to warm up, then 1,000 times one after another, and the times are reported as
// percentiles, with two parts of each apart from the rest: inserting the audit events of the page's reads, and
// committing its transaction, which waits for them to reach the disk. That wait is only there when the database's
// fsync, synchronous_commit and full_page_writes are on, as they are on the stack's primary and in the PostgreSQL the
// benchmark starts itself (MigratedDatabase.Durable; the tests' own have them off). It prints the three as it read
// them, and with any of them off it says so over the table and refuses --assert. A kind of request whose full time's
// p99, the commit included, isn't under the budget gets a warning, in its row and after the table (Warn), which never
// fails the run. The app logs only warnings while it runs, so the console doesn't weigh on the times.
//
// By default it starts its own PostgreSQL 18, migrated as the tests' is, and seeds it. With --stack it uses the web
// app's own settings instead, as `dotnet run --project src/Ehr.Web` does, against a primary already seeded with as many
// patients. Either way it creates a clinician of its own, with a random password nobody keeps, puts 100 of the seeded
// patients on their panel, signs in with password and authenticator code like a browser, and locks the account at the
// end. It knows whom to search for from the synthetic clinic itself, so it reads nobody outside the pages it measures.
internal static partial class RenderBenchmark
{
    // AGENTS.md's gate: each kind of request's p99, excluding the commit, under this (Gate). The full time's p99 not
    // under it only warns (Warn).
    public const double BudgetMilliseconds = 20;

    const int WarmUp = 100;

    const int Measured = 1_000;

    const int PanelSize = 100;

    static readonly Uri Base = new("https://localhost");

    // The exit code: with assert, Gate's. Warn's warnings never change it.
    public static async Task<int> RunAsync(int patients, bool stack, bool assert)
    {
        MigratedDatabase? database = null;
        try
        {
            string? connectionString = null;
            if (!stack)
            {
                Console.WriteLine($"Starting PostgreSQL 18 and seeding {patients:N0} synthetic patients...");
                database = MigratedDatabase.Durable();
                await database.InitializeAsync();
                await Seeder.LoadAsync(database.OwnerConnectionString, new SyntheticClinic(patients), CancellationToken.None);
                connectionString = database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);
            }

            // Against the stack, the certificate is checked before the app starts: whatever starts it, it must not find
            // a key ring it can't read.
            if (stack && StackRefusal(WebAppSettings()) is { } refusal)
            {
                Console.Error.WriteLine(refusal);
                return 2;
            }

            var times = new DatabaseTimes();
            await using var app = new BenchApp(connectionString, times);
            var durability = await ReadDurabilityAsync(app);
            if (assert && !durability.IsOn)
            {
                Console.Error.WriteLine($"--assert needs a database whose commits wait for the disk, and this one has {durability}.");
                return 2;
            }

            if (stack && !await StackIsReadyAsync(app, patients))
            {
                return 2;
            }

            var (clinicianId, email, password, key) = await CreateClinicianAsync(app);
            try
            {
                var panel = await AssignPanelAsync(app, clinicianId, patients);
                using var client = await SignInAsync(app, email, password, key);
                var random = new Random(20261115);
                SyntheticChart Anyone() => SyntheticClinic.Chart(random.Next(patients));
                static string Date(SyntheticChart chart) => chart.Patient.BirthDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                var results = new List<Result>
                {
                    // The chart first, before the searches have written their many audit events.
                    await MeasureAsync(client, times, "Chart, a patient on the panel", typing: false, () => $"/patients/{panel[random.Next(panel.Count)]}"),
                    // The clinic's home with no search: all PanelSize patients on the panel, each a recorded read.
                    await MeasureAsync(client, times, $"Panel, {PanelSize} patients", typing: false, () => "/patients"),
                    await MeasureAsync(client, times, "Search, first 3 letters of a name", typing: false, () => $"/patients?name={Anyone().Patient.FamilyName[..3]}"),
                    await MeasureAsync(client, times, "Search, a whole family name", typing: false, () => $"/patients?name={Anyone().Patient.FamilyName}"),
                    await MeasureAsync(client, times, "Search, date of birth", typing: false, () => $"/patients?birthdate={Date(Anyone())}"),
                    await MeasureAsync(client, times, "Search, MRN", typing: false, () => $"/patients?mrn={SyntheticClinic.Mrn(random.Next(patients))}"),
                    await MeasureAsync(client, times, "Search, name and date of birth", typing: false, () =>
                    {
                        var chart = Anyone();
                        return $"/patients?name={chart.Patient.FamilyName[..4]}&birthdate={Date(chart)}";
                    }),
                    await MeasureAsync(client, times, "As you type, first 3 letters", typing: true, () => $"/patients?handler=Results&name={Anyone().Patient.FamilyName[..3]}"),
                };

                await PrintAsync(app, patients, durability, results);
                Warn(results, Console.Out);
                return assert ? Gate(results, Console.Out) : 0;
            }
            finally
            {
                await LockOutAsync(app, clinicianId);
            }
        }
        finally
        {
            if (database is not null)
            {
                await database.DisposeAsync();
            }
        }
    }

    // Times one kind of request, after warming it up, and its audit events' insert and its commit within it. Every
    // answer must be the page itself.
    static async Task<Result> MeasureAsync(HttpClient client, DatabaseTimes times, string name, bool typing, Func<string> next)
    {
        var addresses = Enumerable.Range(0, WarmUp + Measured).Select(_ => next()).ToList();
        var total = new double[Measured];
        var audit = new double[Measured];
        var commit = new double[Measured];
        for (var i = 0; i < addresses.Count; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, addresses[i]);
            if (typing)
            {
                request.Headers.Add("HX-Request", "true");
            }

            times.Reset();
            var started = Stopwatch.GetTimestamp();
            using var response = await client.SendAsync(request);
            await response.Content.ReadAsByteArrayAsync();
            var elapsed = Stopwatch.GetElapsedTime(started);
            if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri?.AbsolutePath != new Uri(Base, addresses[i]).AbsolutePath)
            {
                throw new InvalidOperationException($"{name}: {addresses[i]} answered {(int)response.StatusCode} at {response.RequestMessage?.RequestUri?.AbsolutePath}.");
            }

            if (i >= WarmUp)
            {
                total[i - WarmUp] = elapsed.TotalMilliseconds;
                audit[i - WarmUp] = times.AuditMilliseconds;
                commit[i - WarmUp] = times.CommitMilliseconds;
            }
        }

        Console.WriteLine($"  {name}: done");
        return new Result(name, total, audit, commit);
    }

    // One kind of request's times: the whole request, and within it the audit events' insert and the commit.
    internal sealed record Result(string Name, double[] Total, double[] Audit, double[] Commit)
    {
        // Each request less its own commit, the audit events' insert still in it: what Gate checks.
        public double[] ExcludingCommit => [.. Total.Select((total, i) => total - Commit[i])];

        // The rest of each request: authentication, the handler's queries, and rendering.
        public double[] Rest => [.. Total.Select((total, i) => total - Audit[i] - Commit[i])];

        public bool OverBudget => Percentile(ExcludingCommit, 0.99) >= BudgetMilliseconds;

        // The whole request's p99, the commit included, not under the budget: a warning (Warn), never a failure.
        public bool FullTimeOverBudget => Percentile(Total, 0.99) >= BudgetMilliseconds;
    }

    // What the table writes at the end of a row: the gate's mark when its p99 excluding the commit isn't under the
    // budget, and a warning when its full time's isn't. A row can have both.
    internal static string Markers(Result result) =>
        (result.OverBudget ? $"  not under {BudgetMilliseconds} ms excluding the commit" : "")
        + (result.FullTimeOverBudget ? $"  warning: full time not under {BudgetMilliseconds} ms" : "");

    // After the table, with or without --assert: a warning for each kind of request whose full time's p99, the commit
    // included, isn't under the budget, and nothing when none is. It never fails the run: that target is checked on the
    // clinic server.
    internal static void Warn(IEnumerable<Result> results, TextWriter output)
    {
        var over = results.Where(result => result.FullTimeOverBudget).ToList();
        if (over.Count == 0)
        {
            return;
        }

        output.WriteLine();
        foreach (var result in over)
        {
            output.WriteLine($"warning: {result.Name}: p99 including the commit {Percentile(result.Total, 0.99):F1} ms, not under {BudgetMilliseconds} ms. "
                + "This doesn't fail the run: the full time's target is checked on the clinic server.");
        }
    }

    // --assert, after the table: every kind of request's p99 excluding the commit must be under the budget. The full
    // time's p99, the commit included, isn't checked here, only warned of (Warn): CI runners' shared disks make commit
    // times unsteady, so that target is checked on the clinic server. Returns the exit code, 1 when a row is over, each
    // of which it names.
    internal static int Gate(IEnumerable<Result> results, TextWriter output)
    {
        var over = results.Where(result => result.OverBudget).ToList();
        output.WriteLine();
        if (over.Count == 0)
        {
            output.WriteLine($"--assert passed: every p99 excluding the commit is under {BudgetMilliseconds} ms.");
            return 0;
        }

        foreach (var result in over)
        {
            output.WriteLine($"--assert failed: {result.Name}: p99 excluding the commit {Percentile(result.ExcludingCommit, 0.99):F1} ms, "
                + $"not under {BudgetMilliseconds} ms.");
        }

        return 1;
    }

    // How long the page spent inserting audit events and committing its transaction, from EF Core's own timing of each
    // command and commit. The requests run one at a time, so what was timed since the last Reset is the current one's.
    internal sealed class DatabaseTimes : IDbCommandInterceptor, IDbTransactionInterceptor
    {
        long auditTicks;

        long commitTicks;

        public double AuditMilliseconds => TimeSpan.FromTicks(Interlocked.Read(ref auditTicks)).TotalMilliseconds;

        public double CommitMilliseconds => TimeSpan.FromTicks(Interlocked.Read(ref commitTicks)).TotalMilliseconds;

        public void Reset()
        {
            Interlocked.Exchange(ref auditTicks, 0);
            Interlocked.Exchange(ref commitTicks, 0);
        }

        public DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            CountAudit(command, eventData);
            return result;
        }

        public ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            CountAudit(command, eventData);
            return ValueTask.FromResult(result);
        }

        public int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
        {
            CountAudit(command, eventData);
            return result;
        }

        public ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            CountAudit(command, eventData);
            return ValueTask.FromResult(result);
        }

        public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
            Interlocked.Add(ref commitTicks, eventData.Duration.Ticks);

        public Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Interlocked.Add(ref commitTicks, eventData.Duration.Ticks);
            return Task.CompletedTask;
        }

        void CountAudit(DbCommand command, CommandExecutedEventData eventData)
        {
            if (command.CommandText.StartsWith("INSERT INTO ehr.audit_events", StringComparison.Ordinal))
            {
                Interlocked.Add(ref auditTicks, eventData.Duration.Ticks);
            }
        }
    }

    // The settings that decide whether a commit waits for the disk, as the app's own connection has them: fsync and
    // full_page_writes for the server, synchronous_commit for the session, where every value but off waits for the
    // local flush.
    internal sealed record Durability(string Fsync, string SynchronousCommit, string FullPageWrites)
    {
        public bool IsOn => Fsync == "on" && SynchronousCommit != "off" && FullPageWrites == "on";

        public override string ToString() => $"fsync={Fsync}, synchronous_commit={SynchronousCommit}, full_page_writes={FullPageWrites}";
    }

    static async Task<Durability> ReadDurabilityAsync(BenchApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var connection = (NpgsqlConnection)scope.ServiceProvider.GetRequiredService<EhrDbContext>().Database.GetDbConnection();
        await connection.OpenAsync();
        return await ReadDurabilityAsync(connection);
    }

    internal static async Task<Durability> ReadDurabilityAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            "SELECT name, setting FROM pg_settings WHERE name IN ('fsync', 'synchronous_commit', 'full_page_writes')", connection);
        var settings = new Dictionary<string, string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                settings[reader.GetString(0)] = reader.GetString(1);
            }
        }

        return new Durability(settings["fsync"], settings["synchronous_commit"], settings["full_page_writes"]);
    }

    static async Task PrintAsync(BenchApp app, int patients, Durability durability, List<Result> results)
    {
        string postgres;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var connection = (NpgsqlConnection)scope.ServiceProvider.GetRequiredService<EhrDbContext>().Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SHOW server_version", connection);
            postgres = (string)(await command.ExecuteScalarAsync())!;
        }

        Console.WriteLine();
        Console.WriteLine($"Server render time, {patients:N0} synthetic patients, {Measured:N0} requests each after {WarmUp} to warm up"
            + (durability.IsOn ? "" : ", durability off: commits don't wait for the disk"));
        Console.WriteLine($"{RuntimeInformation.OSDescription}, {RuntimeInformation.OSArchitecture}, {Environment.ProcessorCount} processors, "
            + $"{RuntimeInformation.FrameworkDescription}, PostgreSQL {postgres}, {durability}");
        Console.WriteLine("Milliseconds. The first four columns are the whole request, the commit included. Excl. commit: each request less");
        Console.WriteLine("its own commit. Audit: inserting the audit events of the page's reads. Commit: committing the page's transaction,");
        Console.WriteLine(durability.IsOn
            ? "which waits for them to reach the disk. Rest: everything else, from authentication to rendering."
            : "which with durability off doesn't wait for the disk. Rest: everything else, from authentication to rendering.");
        Console.WriteLine($"--assert gates on the p99 excluding the commit, which must be under {BudgetMilliseconds} ms. The same target for the whole request,");
        Console.WriteLine("the commit included, is checked on the clinic server, not on CI runners, whose shared disks make commit times unsteady:");
        Console.WriteLine("here a row over it gets a warning, in the row and after the table, which never fails the run.");
        Console.WriteLine();
        Console.WriteLine($"{"",-36}{"p50",7}{"p95",7}{"p99",7}{"max",7}  excl. commit{"p50",6}{"p99",7}  audit{"p50",6}{"p99",7}"
            + $"  commit{"p50",6}{"p99",7}{"max",7}  rest{"p50",6}{"p99",7}");
        foreach (var result in results)
        {
            Console.WriteLine($"{result.Name,-36}{Percentile(result.Total, 0.5),7:F1}{Percentile(result.Total, 0.95),7:F1}{Percentile(result.Total, 0.99),7:F1}{result.Total.Max(),7:F1}"
                + $"              {Percentile(result.ExcludingCommit, 0.5),6:F1}{Percentile(result.ExcludingCommit, 0.99),7:F1}"
                + $"       {Percentile(result.Audit, 0.5),6:F1}{Percentile(result.Audit, 0.99),7:F1}"
                + $"        {Percentile(result.Commit, 0.5),6:F1}{Percentile(result.Commit, 0.99),7:F1}{result.Commit.Max(),7:F1}"
                + $"      {Percentile(result.Rest, 0.5),6:F1}{Percentile(result.Rest, 0.99),7:F1}"
                + Markers(result));
        }
    }

    static double Percentile(double[] values, double quantile)
    {
        var sorted = values.Order().ToArray();
        return sorted[Math.Max(0, (int)Math.Ceiling(quantile * sorted.Length) - 1)];
    }

    // The web app's own settings as `dotnet run` gives it them in Development, where --stack runs it: its user-secrets
    // and the environment. Read without starting it.
    static IConfiguration WebAppSettings() =>
        new ConfigurationBuilder().AddUserSecrets(typeof(Program).Assembly, optional: true).AddEnvironmentVariables().Build();

    // Why a run against the stack must not start, or null: without the Data Protection certificate the web app can't
    // read the stack's key ring, which both of the stack's apps share.
    internal static string? StackRefusal(IConfiguration settings) =>
        settings["Ehr:DataProtection:Certificate"] is { Length: > 0 }
            ? null
            : "--stack needs the web app's Ehr:DataProtection:Certificate and CertificateKey settings, as \"Running the apps\" in "
                + "README.md sets them, so that it can read the stack's key ring.";

    // Before a run against the stack writes anything, once the app has started (BenchApp never writes a key): the database
    // must look like the synthetic clinic of that size. It can't read a patient before it has a clinician (row-level
    // security), so it goes by the table's statistics, which the seed gathers; then the first panel assignment, of a
    // synthetic patient's ID, stops the run at the foreign key if the clinic isn't the synthetic one. Then it says what
    // it writes.
    static async Task<bool> StackIsReadyAsync(BenchApp app, int patients)
    {
        var configuration = app.Services.GetRequiredService<IConfiguration>();
        long rows;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var connection = (NpgsqlConnection)scope.ServiceProvider.GetRequiredService<EhrDbContext>().Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT reltuples::bigint FROM pg_class WHERE oid = 'ehr.patients'::regclass", connection);
            rows = (long)(await command.ExecuteScalarAsync())!;
        }

        if (Math.Abs(rows - patients) > patients / 100)
        {
            Console.Error.WriteLine(
                $"The database's statistics give ehr.patients about {rows:N0} rows, not the {patients:N0} of the synthetic clinic: "
                + "point --stack at a primary seeded with `seed`, and give --patients the same number.");
            return false;
        }

        var target = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Ehr"));
        Console.WriteLine(
            $"Writing to {target.Host}:{target.Port}/{target.Database} as {target.Username}: one staff account, locked at the end; "
            + $"{PanelSize} panel assignments, each with its write-audit event; and an audit event for every patient each page lists, "
            + "about 195,000 at 50,000 patients. Nothing deletes them.");
        return true;
    }

    static async Task<(Guid Id, string Email, string Password, string Key)> CreateClinicianAsync(BenchApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        var email = $"bench-{Guid.NewGuid():N}@example.test";
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var user = new StaffUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = "Benchmark", Roles = [StaffRoles.Clinician] };
        Check(await users.CreateAsync(user, password));
        Check(await users.ResetAuthenticatorKeyAsync(user));
        var key = (await users.GetAuthenticatorKeyAsync(user))!;
        Check(await users.SetTwoFactorEnabledAsync(user, true));
        return (user.Id, email, password, key);
    }

    // Puts PanelSize patients, spread across the clinic, on the clinician's panel, as the clinician, so their charts
    // open without asking for a reason. Their IDs come from the synthetic clinic, not from the database.
    static async Task<List<Guid>> AssignPanelAsync(BenchApp app, Guid clinicianId, int patients)
    {
        var panel = Enumerable.Range(0, Math.Min(PanelSize, patients))
            .Select(i => SyntheticClinic.Chart(i * (patients / Math.Min(PanelSize, patients))).Patient.Id)
            .ToList();
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<UserContext>().UserId = clinicianId;
        var db = scope.ServiceProvider.GetRequiredService<EhrDbContext>();
        var assignments = scope.ServiceProvider.GetRequiredService<PatientAssignments>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        foreach (var patient in panel)
        {
            await assignments.AssignAsync(patient, CancellationToken.None);
        }

        await transaction.CommitAsync();
        return panel;
    }

    // Signs in as a browser does, password then authenticator code, and returns a client with the session's cookies.
    static async Task<HttpClient> SignInAsync(BenchApp app, string email, string password, string key)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = Base, HandleCookies = true, AllowAutoRedirect = true });
        using var codePage = await PostFormAsync(client, "/sign-in", await client.GetStringAsync("/sign-in"), ("Input.Email", email), ("Input.Password", password));
        using var home = await PostFormAsync(client, "/sign-in/code", await codePage.Content.ReadAsStringAsync(), ("Code", Totp.Code(key)));
        if (home.RequestMessage?.RequestUri?.AbsolutePath != "/patients")
        {
            throw new InvalidOperationException($"Signing in ended at {home.RequestMessage?.RequestUri?.AbsolutePath}, not at /patients.");
        }

        return client;
    }

    static Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path, string page, params (string Name, string Value)[] fields)
    {
        var token = FormToken().Match(page);
        if (!token.Success)
        {
            throw new InvalidOperationException($"{path} has no form token.");
        }

        return client.PostAsync(path, new FormUrlEncodedContent(
            [.. fields.Select(field => KeyValuePair.Create(field.Name, field.Value)), KeyValuePair.Create("__RequestVerificationToken", token.Groups[1].Value)]));
    }

    // The benchmark's clinician can't sign in again: locked out, as an admin disables an account.
    static async Task LockOutAsync(BenchApp app, Guid clinicianId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        if (await users.FindByIdAsync(clinicianId.ToString()) is { } user)
        {
            Check(await users.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue));
        }
    }

    static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex FormToken();

    // The on-prem app in memory: against the PostgreSQL the benchmark started, as Production, or, without a connection
    // string, as Development, which reads the web app's user-secrets: the stack's primary, as `dotnet run` uses it. The
    // content root is named outright: WebApplicationFactory looks for it from the current folder, which `dotnet run`
    // leaves at the repository's, not the build output's, as a test runner does.
    //
    // It never writes a Data Protection key to the database it points at. Its own cookies and form tokens use keys kept in
    // memory, and the key ring in the database, which the stack's apps share, is only ever read: the app doesn't create a
    // key there at startup, as it otherwise would when it can't read the keys it finds. Its startup check of that key ring,
    // which then finds no key it may use, logs only what is critical: nothing here uses those keys.
    internal sealed class BenchApp(string? connectionString, DatabaseTimes? times = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder
                .UseContentRoot(RepositoryRoot.Combine("src/Ehr.Web"))
                .UseSetting("Ehr:Site", "OnPrem")
                .UseSetting("Logging:LogLevel:Default", "Warning")
                .UseSetting("Logging:LogLevel:Microsoft.AspNetCore.DataProtection", "Critical")
                .ConfigureTestServices(services =>
                {
                    services.Configure<KeyManagementOptions>(options => options.AutoGenerateKeys = false);
                    services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                    if (times is not null)
                    {
                        services.ConfigureDbContext<EhrDbContext>(options => options.AddInterceptors(times));
                    }
                });
            if (connectionString is not null)
            {
                builder.UseEnvironment(Environments.Production).UseSetting("ConnectionStrings:Ehr", connectionString);
            }
        }
    }
}
