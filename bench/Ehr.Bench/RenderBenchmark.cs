using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Data.Orders;
using Ehr.Domain.Audit;
using Ehr.Domain.Patients;
using Ehr.Seed;
using Ehr.Testing;
using Ehr.Web.CloudAudit;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Ehr.Bench;

// Server render time for the patient search, the panel, the chart and the portal's results page in a clinic of 50,000
// synthetic patients (NFR-01, NFR-04; AGENTS.md's gate, which --assert checks, is in Gate), on-site and off-site
// (NFR-03). The apps run in memory, so a request's time is the server's own: authentication, the page's queries and
// the reads it records, and rendering, with no network in it.
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
// patients on their panel, signs six orders for each of them and discontinues two (UC 1), so the chart it measures lists active orders,
// signs in with password and authenticator code like a browser, and locks the account at the end. It knows whom to search for from the synthetic clinic itself, so it reads nobody outside the pages it measures.
//
// UC 2, off-site (Tahseen Arnan): then the same requests, in the same order, to the cloud app (OffSiteApp), reading as
// ehr_read with its link to the on-prem app down, as in an on-site outage, with the same session. Each row's name starts
// "Off-site". The cloud app records its reads in its audit queue, a file flushed to disk before the page is sent, not in
// the database: so the audit insert is 0, and the queue's append counts as the commit (TimedAuditQueue), since that is
// where the page waits for the disk, as an on-prem page does at its commit. The gate is the same for these rows. Only in
// the PostgreSQL it starts itself, which stands in for the replica as in the web tests: --stack has no ehr_read login.
internal static partial class RenderBenchmark
{
    // AGENTS.md's gate: each kind of request's p99, excluding the commit, under this (Gate). The full time's p99 not
    // under it only warns (Warn).
    public const double BudgetMilliseconds = 20;

    const int WarmUp = 100;

    const int Measured = 1_000;

    const int PanelSize = 100;

    static readonly Uri Base = new("https://localhost");

    // The exit code: Finish's.
    public static async Task<int> RunAsync(int patients, bool stack, bool assert, string? resultsPath = null)
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
                var seeding = Stopwatch.StartNew();
                var seeded = await Seeder.LoadAsync(database.OwnerConnectionString, new SyntheticClinic(patients), CancellationToken.None);
                Console.WriteLine($"Seeded in {seeding.Elapsed.TotalSeconds:F1} s: {string.Join(", ", seeded.Select(table => $"{table.Value:N0} {table.Key}"))}");
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
                var labOrders = await SignOrdersAsync(app, clinicianId, panel);
                await FillInboxAsync(database, clinicianId, panel);
                var (scheduleDay, appointments) = await BookDayAsync(app, clinicianId, panel);
                var threads = await WriteThreadsAsync(app, clinicianId, panel);
                var session = new CookieContainer();
                using var client = await SignInAsync(app, session, email, password, key);
                await SettleAsync(database);
                var results = await MeasureEachAsync(client, times, "", panel, labOrders, patients);
                results.AddRange(await MeasureScheduleAsync(client, times, "", panel, scheduleDay, appointments));
                results.Add(await MeasureAsync(client, times, "Proxy access, a patient on the panel", typing: false, () => $"/patients/{panel[0]}/proxies"));
                // A conversation's page for the clinician (FR-18): its messages, and a recorded read of the thread.
                var threadRandom = new Random(20261119);
                results.Add(await MeasureAsync(client, times, "Conversation, a patient on the panel", typing: false, () =>
                {
                    var (patient, thread) = threads[threadRandom.Next(threads.Count)];
                    return $"/patients/{patient}/messages/{thread}";
                }));
                // The patient portal's results page (UC6, FR-16; Simar Rekhi), as a patient with released results, the
                // last patient in the clinic who has some: the local stack's example account uses the first. On-site, so before the
                // off-site rows.
                var (patientUserId, patientEmail, patientPassword, portalPatient) = await CreatePatientAccountAsync(app, patients);
                var portalThread = await WritePortalThreadAsync(app, clinicianId, portalPatient);
                // Proxy access (FR-19): the clinician records the portal patient as a proxy for one of the panel.
                var proxyGrant = await RecordProxyAsync(app, clinicianId, panel[0], patientEmail);
                try
                {
                    using var portal = await PortalSignInAsync(app, patientEmail, patientPassword);
                    results.Add(await MeasureAsync(portal, times, "Portal results, the patient's own", typing: false, () => "/portal/results"));
                    results.Add(await MeasureAsync(portal, times, "Portal appointments, the patient's own", typing: false, () => "/portal/appointments"));
                    results.Add(await MeasureAsync(portal, times, "Portal home, the patient's own", typing: false, () => "/portal"));
                    results.Add(await MeasureAsync(portal, times, "Portal messages, the patient's own", typing: false, () => "/portal/messages"));
                    results.Add(await MeasureAsync(portal, times, "Portal conversation, the patient's own", typing: false, () => $"/portal/messages/{portalThread}"));
                    results.Add(await MeasureAsync(portal, times, "Portal access, the patient's own", typing: false, () => "/portal/access"));
                    // The same session, now looking at the record it holds a proxy grant for: every page first checks
                    // the grant, and every policy resolves the subject through it.
                    using (var chosen = await PostFormAsync(portal, "/portal/acting", await portal.GetStringAsync("/portal/access"), ("grantId", proxyGrant.ToString())))
                    {
                        if (chosen.RequestMessage?.RequestUri?.AbsolutePath != "/portal")
                        {
                            throw new InvalidOperationException("Choosing the proxied record did not end at /portal.");
                        }
                    }

                    results.Add(await MeasureAsync(portal, times, "Portal home, as a proxy", typing: false, () => "/portal"));
                    results.Add(await MeasureAsync(portal, times, "Portal results, as a proxy", typing: false, () => "/portal/results"));
                }
                finally
                {
                    await LockOutPatientAsync(app, patientUserId);
                }

                if (database is null)
                {
                    Console.WriteLine("Off-site: not measured with --stack, which has no ehr_read login for the replica.");
                }
                else
                {
                    await SettleAsync(database);
                    results.AddRange(await MeasureOffSiteAsync(app, database, session, panel, labOrders, patients, scheduleDay, appointments));
                }

                await PrintAsync(app, patients, durability, results);
                if (resultsPath is not null)
                {
                    await File.WriteAllTextAsync(resultsPath, ResultsJson(patients, durability, results));
                }

                return Finish(results, assert, Console.Out);
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

    // Before each set of rows, in the PostgreSQL the benchmark started itself: lets the database finish the housekeeping
    // that what came before left it (the seed's 50,000 patients before the on-site rows; the on-site rows' audit events,
    // some 200,000, before the off-site ones), so that isn't billed to the first row measured. Without it the first
    // off-site row, the chart, sometimes ran while PostgreSQL was still vacuuming, analyzing and checkpointing after
    // the on-site rows, which a replica on its own server wouldn't be doing for them; its p99 went from about 13 ms to
    // over 30 in one run of two. VACUUM (ANALYZE) and CHECKPOINT do that work now instead of during the rows, and the
    // .NET heap is collected for the same reason. Not with --stack: that database is somebody's, and is left alone.
    static async Task SettleAsync(MigratedDatabase? database)
    {
        if (database is null)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync();
        foreach (var statement in new[] { "VACUUM (ANALYZE)", "CHECKPOINT" })
        {
            await using var command = new NpgsqlCommand(statement, connection) { CommandTimeout = 600 };
            await command.ExecuteNonQueryAsync();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    // Every kind of request, each row's name after the prefix. The random numbers start from the same seed each time, so
    // on-site and off-site make the same requests.
    static async Task<List<Result>> MeasureEachAsync(
        HttpClient client, DatabaseTimes times, string prefix, List<Guid> panel, IReadOnlyDictionary<Guid, Guid> labOrders, int patients)
    {
        var random = new Random(20261115);
        SyntheticChart Anyone() => SyntheticClinic.Chart(random.Next(patients));
        static string Date(SyntheticChart chart) => chart.Patient.BirthDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return
        [
            // The chart first, before the searches have written their many audit events.
            await MeasureAsync(client, times, prefix + "Chart, a patient on the panel", typing: false, () => $"/patients/{panel[random.Next(panel.Count)]}"),
            // The clinic's home with no search: all PanelSize patients on the panel, each a recorded read.
            await MeasureAsync(client, times, prefix + $"Panel, {PanelSize} patients", typing: false, () => "/patients"),
            await MeasureAsync(client, times, prefix + "Search, first 3 letters of a name", typing: false, () => $"/patients?name={Anyone().Patient.FamilyName[..3]}"),
            await MeasureAsync(client, times, prefix + "Search, a whole family name", typing: false, () => $"/patients?name={Anyone().Patient.FamilyName}"),
            await MeasureAsync(client, times, prefix + "Search, date of birth", typing: false, () => $"/patients?birthdate={Date(Anyone())}"),
            await MeasureAsync(client, times, prefix + "Search, MRN", typing: false, () => $"/patients?mrn={SyntheticClinic.Mrn(random.Next(patients))}"),
            await MeasureAsync(client, times, prefix + "Search, name and date of birth", typing: false, () =>
            {
                var chart = Anyone();
                return $"/patients?name={chart.Patient.FamilyName[..4]}&birthdate={Date(chart)}";
            }),
            await MeasureAsync(client, times, prefix + "As you type, first 3 letters", typing: true, () => $"/patients?handler=Results&name={Anyone().Patient.FamilyName[..3]}"),
            // UC 1's order form (Tahseen Arnan), last so the rows before it make the requests they always did: the patient's
            // banner and allergies, one recorded read, and the lab catalog.
            await MeasureAsync(client, times, prefix + "Order form, a patient on the panel", typing: false, () => $"/patients/{panel[random.Next(panel.Count)]}/orders/new?type=lab"),
            // UC 1's changes to a signed order, after everything else for the same reason: the patient's discontinued
            // orders, and the forms that change and discontinue the patient's active lab order. Each is one recorded read.
            await MeasureAsync(client, times, prefix + "Discontinued orders, a patient on the panel", typing: false,
                () => $"/patients/{panel[random.Next(panel.Count)]}/orders/discontinued"),
            await MeasureAsync(client, times, prefix + "Change order form, a patient on the panel", typing: false, () =>
            {
                var patient = panel[random.Next(panel.Count)];
                return $"/patients/{patient}/orders/new?type=lab&replaces={labOrders[patient]}";
            }),
            await MeasureAsync(client, times, prefix + "Discontinue order form, a patient on the panel", typing: false, () =>
            {
                var patient = panel[random.Next(panel.Count)];
                return $"/patients/{patient}/orders/{labOrders[patient]}/discontinue";
            }),
            // Editing the chart's lists (the expansion plan's step 2): the chart with its sensitive diagnoses and
            // medications shown, which records a second audit event; the form that adds a diagnosis; the search of the
            // reference codes as a clinician types, which reads nothing about a patient; and the lists' history.
            await MeasureAsync(client, times, prefix + "Chart with sensitive items, a patient on the panel", typing: false,
                () => $"/patients/{panel[random.Next(panel.Count)]}?sensitive=true"),
            await MeasureAsync(client, times, prefix + "Chart entry form, a patient on the panel", typing: false,
                () => $"/patients/{panel[random.Next(panel.Count)]}/diagnoses"),
            await MeasureAsync(client, times, prefix + "Code search, first 3 letters", typing: false,
                () => $"/codes?kind=condition&q={CodeSearches[random.Next(CodeSearches.Length)]}"),
            // The same search by words inside the names, which goes through the text-search index.
            await MeasureAsync(client, times, prefix + "Code search, words inside the name", typing: false,
                () => $"/codes?kind=condition&q={Uri.EscapeDataString(CodeWords[random.Next(CodeWords.Length)])}"),
            // The staff inbox (the expansion plan's step 3): its items, each naming a patient, which is a recorded read
            // of each. Every row above also counted the inbox for the app bar.
            await MeasureAsync(client, times, prefix + $"Inbox, {InboxSize} items", typing: false, () => "/inbox"),
            await MeasureAsync(client, times, prefix + "Chart history, a patient on the panel", typing: false,
                () => $"/patients/{panel[random.Next(panel.Count)]}/chart-history"),
        ];
    }

    // What a clinician might have typed so far when looking for a diagnosis.
    static readonly string[] CodeSearches = ["typ", "ess", "anx", "ast", "hyp", "dia", "dep", "alc", "low", "mig"];

    static readonly string[] CodeWords = ["diabetes", "mellitus compl", "fracture femur", "pneumonia", "kidney chronic", "pain", "infarction", "left", "unspecified site", "encounter"];

    // UC 2 (Tahseen Arnan): the same requests to the cloud app, with the on-site session's cookies, during an outage. Its
    // audit queue goes in a folder of its own, deleted at the end.
    static async Task<List<Result>> MeasureOffSiteAsync(
        BenchApp onPrem, MigratedDatabase database, CookieContainer session, List<Guid> panel, IReadOnlyDictionary<Guid, Guid> labOrders, int patients,
        string scheduleDay, List<Guid> appointments)
    {
        var queue = Path.Combine(Path.GetTempPath(), $"ehr-bench-cloud-audit-{Guid.NewGuid():N}");
        try
        {
            var times = new DatabaseTimes();
            await using var cloud = new OffSiteApp(database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword), times, onPrem.Keys, queue);
            // The check the cloud app makes every couple of seconds, now, so every page measured says read-only.
            if (await cloud.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(CancellationToken.None))
            {
                throw new InvalidOperationException("The off-site app reached the on-prem app through a link that is down.");
            }

            using var client = cloud.CreateDefaultClient(Base, new RedirectHandler(), new CookieContainerHandler(session));
            var results = await MeasureEachAsync(client, times, "Off-site, ", panel, labOrders, patients);
            results.AddRange(await MeasureScheduleAsync(client, times, "Off-site, ", panel, scheduleDay, appointments));
            return results;
        }
        finally
        {
            if (Directory.Exists(queue))
            {
                Directory.Delete(queue, recursive: true);
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

    // After the table: the warnings, then, with --assert, the gate. Returns the exit code, Gate's with --assert and 0
    // without; the warnings never change it.
    // The run as --results writes it, for the performance test cases (tests/Ehr.Web.Tests/TestCases), which check each
    // path's p99 against its own requirement's limit: how many patients, whether commits waited for the disk, and each
    // row's percentiles in milliseconds, for the whole request and excluding the commit.
    internal static string ResultsJson(int patients, Durability durability, IEnumerable<Result> results) =>
        JsonSerializer.Serialize(
            new
            {
                patients,
                durabilityOn = durability.IsOn,
                durability = durability.ToString(),
                warmUp = WarmUp,
                measured = Measured,
                rows = results.Select(result => new
                {
                    name = result.Name,
                    p50 = Math.Round(Percentile(result.Total, 0.50), 1),
                    p99 = Math.Round(Percentile(result.Total, 0.99), 1),
                    p50ExcludingCommit = Math.Round(Percentile(result.ExcludingCommit, 0.50), 1),
                    p99ExcludingCommit = Math.Round(Percentile(result.ExcludingCommit, 0.99), 1),
                }),
            },
            new JsonSerializerOptions { WriteIndented = true });

    internal static int Finish(IEnumerable<Result> results, bool assert, TextWriter output)
    {
        Warn(results, output);
        return assert ? Gate(results, output) : 0;
    }

    // After the table, with or without --assert: a warning for each kind of request whose full time's p99, the commit
    // included, isn't under the budget, and nothing when none is. It never fails the run: on the clinic server it is the
    // target to meet, and on CI runners shared disks make commit times unsteady.
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
                + "This doesn't fail the run. On the clinic server, this is the target to meet; on CI runners, shared disks make commit times unsteady.");
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

        // Off-site, the audit queue's append (TimedAuditQueue).
        public void AddCommit(TimeSpan duration) => Interlocked.Add(ref commitTicks, duration.Ticks);

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

    // UC 2 (Tahseen Arnan): the cloud app's audit queue, its append timed as the request's commit. The append writes the
    // page's events and flushes them to disk before the page is sent (CloudAuditQueue), as an on-prem page's commit waits
    // for its audit events to reach the disk; writing a few lines of JSON is a small part of it.
    internal sealed class TimedAuditQueue(IAuditQueue queue, DatabaseTimes times) : IAuditQueue
    {
        public void Enqueue(IReadOnlyCollection<AuditEvent> auditEvents)
        {
            var started = Stopwatch.GetTimestamp();
            queue.Enqueue(auditEvents);
            times.AddCommit(Stopwatch.GetElapsedTime(started));
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
        Console.WriteLine("the commit included, is checked on the clinic server, not on CI runners, whose shared disks make commit times unsteady.");
        Console.WriteLine("A row over it gets a warning, in the row and after the table, which never fails the run.");
        if (results.Any(result => result.Name.StartsWith("Off-site", StringComparison.Ordinal)))
        {
            Console.WriteLine("Off-site: the cloud app reading as ehr_read, its link to the on-prem app down. It records reads in its audit queue, not");
            Console.WriteLine("the database: Audit is 0, and Commit is its read-only transaction's commit plus the queue's write and flush to disk.");
        }

        Console.WriteLine();
        Console.WriteLine($"{"",-58}{"p50",7}{"p95",7}{"p99",7}{"max",7}  excl. commit{"p50",6}{"p99",7}  audit{"p50",6}{"p99",7}"
            + $"  commit{"p50",6}{"p99",7}{"max",7}  rest{"p50",6}{"p99",7}");
        foreach (var result in results)
        {
            Console.WriteLine($"{result.Name,-58}{Percentile(result.Total, 0.5),7:F1}{Percentile(result.Total, 0.95),7:F1}{Percentile(result.Total, 0.99),7:F1}{result.Total.Max(),7:F1}"
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

    // A confirmed portal account, with a random password nobody keeps, for the last patient in the clinic with at least
    // three released results.
    static async Task<(Guid Id, string Email, string Password, Guid Patient)> CreatePatientAccountAsync(BenchApp app, int patients)
    {
        var patient = Enumerable.Range(0, patients).Reverse()
            .Select(SyntheticClinic.Chart)
            .First(chart => chart.Results.Count(r => r.IssuedAt is not null) >= 3)
            .Patient.Id;
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PatientUser>>();
        var email = $"bench-patient-{Guid.NewGuid():N}@example.test";
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var user = new PatientUser { UserName = email, Email = email, EmailConfirmed = true, PatientId = patient };
        Check(await users.CreateAsync(user, password));
        return (user.Id, email, password, patient);
    }

    // Signs a patient in at the portal, as a browser does, and returns a client with the session's cookies.
    static async Task<HttpClient> PortalSignInAsync(BenchApp app, string email, string password)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = Base, HandleCookies = true, AllowAutoRedirect = true });
        using var home = await PostFormAsync(client, "/portal/sign-in", await client.GetStringAsync("/portal/sign-in"), ("Input.Email", email), ("Input.Password", password));
        if (home.RequestMessage?.RequestUri?.AbsolutePath != "/portal")
        {
            throw new InvalidOperationException($"Signing in at the portal ended at {home.RequestMessage?.RequestUri?.AbsolutePath}, not at /portal.");
        }

        return client;
    }

    // The benchmark's patient account can't sign in again either.
    static async Task LockOutPatientAsync(BenchApp app, Guid patientUserId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PatientUser>>();
        if (await users.FindByIdAsync(patientUserId.ToString()) is { } user)
        {
            Check(await users.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue));
        }
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

    // UC 1 Place an order (Tahseen Arnan): one active order of each type for every patient on the panel, signed as the
    // clinician through the database's own signing trigger and policies, so the chart the benchmark measures has its
    // list of active orders to load; and two more that are then discontinued, through the database's own rules for
    // that, so the discontinued orders' page has rows to load. The seed can't make them: an order's signer is a staff
    // account, and the seeded clinic has none. With --stack they stay on the primary afterwards, as the benchmark's
    // audit events do. Returns each patient's active lab order, for the forms that change and discontinue it.
    static async Task<Dictionary<Guid, Guid>> SignOrdersAsync(BenchApp app, Guid clinicianId, List<Guid> panel)
    {
        var items = OrderType.All.Select(type => OrderCatalogItem.Seeded.First(item => item.Type == type)).ToList();
        var stopped = OrderType.All.Take(2).Select(type => OrderCatalogItem.Seeded.Last(item => item.Type == type)).ToList();
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<UserContext>().UserId = clinicianId;
        var db = scope.ServiceProvider.GetRequiredService<EhrDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var labOrders = new Dictionary<Guid, Guid>();
        var discontinued = new List<Guid>();
        foreach (var patient in panel)
        {
            foreach (var item in items.Concat(stopped))
            {
                var order = new Order
                {
                    Id = Guid.CreateVersion7(),
                    PatientId = patient,
                    Type = item.Type,
                    CatalogCode = item.Code,
                    Details = "{}",
                    Status = OrderStatus.Active,
                };
                db.Add(order);
                if (stopped.Contains(item))
                {
                    discontinued.Add(order.Id);
                }
                else if (item.Type == OrderType.Lab)
                {
                    labOrders[patient] = order.Id;
                }
            }
        }

        await db.SaveChangesAsync();
        await db.Orders.Where(order => discontinued.Contains(order.Id)).ExecuteUpdateAsync(set => set
            .SetProperty(order => order.Status, OrderStatus.Revoked)
            .SetProperty(order => order.DiscontinueReason, "Synthetic: no longer needed"));
        await transaction.CommitAsync();
        return labOrders;
    }

    // A conversation of six messages, all from staff (a clinician can't write as the patient), for each of the first
    // 24 patients on the panel, written through the database's own rules.
    static async Task<List<(Guid Patient, Guid Thread)>> WriteThreadsAsync(BenchApp app, Guid clinicianId, List<Guid> panel)
    {
        var threads = new List<(Guid, Guid)>();
        foreach (var patient in panel.Take(DaySize))
        {
            threads.Add((patient, await WriteThreadAsync(app, clinicianId, patient)));
        }

        return threads;
    }

    static async Task<Guid> WriteThreadAsync(BenchApp app, Guid clinicianId, Guid patient)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<UserContext>().UserId = clinicianId;
        var db = scope.ServiceProvider.GetRequiredService<EhrDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var thread = new Ehr.Data.Messaging.MessageThread { Id = Guid.CreateVersion7(), PatientId = patient, Subject = "Synthetic: follow-up" };
        db.Add(thread);
        await db.SaveChangesAsync();
        db.AddRange(Enumerable.Range(0, 6).Select(i => new Ehr.Data.Messaging.Message
        {
            Id = Guid.CreateVersion7(),
            ThreadId = thread.Id,
            PatientId = patient,
            SenderKind = Ehr.Data.Messaging.MessageSender.Staff,
            Body = $"Synthetic message {i + 1}: your results are in, and we can go over them at your next visit.",
        }));
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return thread.Id;
    }

    // The clinician records a proxy for a patient on their panel, through the app's own service and the database's rules.
    static async Task<Guid> RecordProxyAsync(BenchApp app, Guid clinicianId, Guid patient, string proxyEmail)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<UserContext>().UserId = clinicianId;
        var db = scope.ServiceProvider.GetRequiredService<EhrDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var access = scope.ServiceProvider.GetRequiredService<Ehr.Domain.Proxies.ProxyAccess>();
        if (await access.GiveAsync(patient, [], proxyEmail, Ehr.Data.Proxies.ProxyScope.RecordAndMessages, includeSensitive: false, 30, CancellationToken.None)
            != Ehr.Domain.Proxies.GiveResult.Given)
        {
            throw new InvalidOperationException("The benchmark's proxy grant was refused.");
        }

        var grant = await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM ehr.proxy_grants WHERE patient_id = {patient} AND revoked_at IS NULL").SingleAsync();
        await transaction.CommitAsync();
        return grant;
    }

    // The portal patient's own conversation: the clinician puts them on the panel first, since only the care team writes.
    static async Task<Guid> WritePortalThreadAsync(BenchApp app, Guid clinicianId, Guid patient)
    {
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<UserContext>().UserId = clinicianId;
            var db = scope.ServiceProvider.GetRequiredService<EhrDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await scope.ServiceProvider.GetRequiredService<PatientAssignments>().AssignAsync(patient, CancellationToken.None);
            await transaction.CommitAsync();
        }

        return await WriteThreadAsync(app, clinicianId, patient);
    }

    const int DaySize = 24;

    // A day of appointments for the benchmark's clinician (FR-17), each with a different patient on their panel,
    // booked through the database's own rules, so the schedule has a full day to list and the charts an appointment
    // to show. With --stack they stay on the primary afterwards, as its orders do.
    static async Task<(string Day, List<Guid> Appointments)> BookDayAsync(BenchApp app, Guid clinicianId, List<Guid> panel)
    {
        var day = DateTime.UtcNow.Date.AddDays(7);
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<UserContext>().UserId = clinicianId;
        var db = scope.ServiceProvider.GetRequiredService<EhrDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var appointments = panel.Take(DaySize).Select((patient, slot) => new Ehr.Data.Scheduling.Appointment
        {
            Id = Guid.CreateVersion7(),
            PatientId = patient,
            ClinicianId = clinicianId,
            StartsAt = new DateTimeOffset(day.AddHours(8).AddMinutes(20 * slot), TimeSpan.Zero),
            Minutes = 20,
            Reason = "Synthetic: follow-up",
            Status = Ehr.Data.Scheduling.AppointmentStatus.Booked,
        }).ToList();
        db.AddRange(appointments);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return (day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), [.. appointments.Select(appointment => appointment.Id)]);
    }

    // The schedule's pages: a clinician's day, each patient named a recorded read; the form that books an appointment;
    // and one appointment's page.
    static async Task<List<Result>> MeasureScheduleAsync(HttpClient client, DatabaseTimes times, string prefix, List<Guid> panel, string day, List<Guid> appointments)
    {
        var random = new Random(20261118);
        return
        [
            await MeasureAsync(client, times, prefix + $"Schedule, a day of {DaySize} appointments", typing: false, () => $"/schedule?day={day}"),
            await MeasureAsync(client, times, prefix + "Book appointment form, a patient on the panel", typing: false,
                () => $"/patients/{panel[random.Next(panel.Count)]}/appointments/new"),
            await MeasureAsync(client, times, prefix + "Appointment, a patient on the panel", typing: false,
                () => $"/appointments/{appointments[random.Next(appointments.Count)]}"),
        ];
    }

    const int InboxSize = 40;

    // Items in the benchmark clinician's inbox, each about a different patient on their panel, so the inbox page has
    // names to read and the app bar a count. Nothing at runtime creates items yet, so they go in as the database's
    // superuser, in the benchmark's own database only: with --stack the inbox is measured empty.
    static async Task FillInboxAsync(MigratedDatabase? database, Guid clinicianId, List<Guid> panel)
    {
        if (database is null)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ehr.inbox_items (id, recipient_id, kind, patient_id, summary, created_at)
            SELECT uuidv7(), @recipient, 'message', patient, 'Synthetic: a message to read', now() - make_interval(mins => n::int)
            FROM unnest(@patients) WITH ORDINALITY AS p(patient, n)
            """, connection);
        command.Parameters.AddWithValue("recipient", clinicianId);
        command.Parameters.AddWithValue("patients", panel.Take(InboxSize).ToArray());
        await command.ExecuteNonQueryAsync();
    }

    // Signs in as a browser does, password then authenticator code, and returns a client with the session's cookies, which
    // it keeps in the jar given, for the off-site app too.
    static async Task<HttpClient> SignInAsync(BenchApp app, CookieContainer session, string email, string password, string key)
    {
        var client = app.CreateDefaultClient(Base, new RedirectHandler(), new CookieContainerHandler(session));
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
        // The keys in memory, which the off-site app shares, as the stack's two apps share theirs.
        public IDataProtectionProvider Keys { get; } = new EphemeralDataProtectionProvider();

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
                    services.AddSingleton(Keys);
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

    // UC 2 (Tahseen Arnan): the cloud app in memory, as Production, reading the database as ehr_read with the on-prem
    // app's keys, its link to the on-prem app down from the start, and its audit queue in the folder given, timed
    // (TimedAuditQueue). Its shipper's warning every couple of seconds that the on-prem app can't be reached is expected
    // here, and left out of the console.
    internal sealed class OffSiteApp(string connectionString, DatabaseTimes times, IDataProtectionProvider keys, string queueDirectory)
        : WebApplicationFactory<Program>
    {
        const string OnPremUrl = "https://onprem.invalid";

        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder
                .UseContentRoot(RepositoryRoot.Combine("src/Ehr.Web"))
                .UseEnvironment(Environments.Production)
                .UseSetting("Ehr:Site", "Cloud")
                .UseSetting("ConnectionStrings:Ehr", connectionString)
                .UseSetting("Ehr:OnPrem:Url", OnPremUrl)
                .UseSetting("Ehr:CloudAudit:Key", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))
                .UseSetting("Ehr:CloudAudit:Directory", queueDirectory)
                .UseSetting("Logging:LogLevel:Default", "Warning")
                .UseSetting("Logging:LogLevel:Microsoft.AspNetCore.DataProtection", "Critical")
                .UseSetting("Logging:LogLevel:Ehr.Web.CloudAudit.CloudAuditShipper", "Error")
                .ConfigureTestServices(services =>
                {
                    services.AddSingleton(keys);
                    services.AddSingleton(new OnPremApp(OnPremUrl, new HttpMessageInvoker(new LinkDown())));
                    services.AddSingleton<IAuditQueue>(provider => new TimedAuditQueue(provider.GetRequiredService<CloudAuditQueue>(), times));
                    services.ConfigureDbContext<EhrDbContext>(options => options.AddInterceptors(times));
                });

        sealed class LinkDown : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new HttpRequestException("The link to the on-prem app is down.", new SocketException((int)SocketError.ConnectionRefused));
        }
    }
}
