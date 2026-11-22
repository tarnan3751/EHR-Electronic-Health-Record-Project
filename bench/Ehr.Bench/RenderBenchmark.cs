using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Data.Orders;
using Ehr.Data.Patients;
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
// percentiles. One that takes over a minute in all stops the run (RowLimit): at the budget's 20 ms each, its 1,100
// requests take 22 seconds, so a query gone wrong fails the run at once instead of holding CI until it times out. The
// percentiles have two parts of each request apart from the rest: inserting the audit events of the page's reads, and
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

    static readonly TimeSpan RowLimit = TimeSpan.FromMinutes(1);

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
                // Results of orders (step 7): 24 resulted lipid panels the clinician signed, half with a value outside its
                // range. Their results page, and the inbox while it holds them (it sorts abnormal ones first). Then they
                // are acknowledged, so the rows measured after this find the inbox as it was.
                var resulted = await FileResultsAsync(app, clinicianId, panel);
                var resultRandom = new Random(20261120);
                results.Add(await MeasureAsync(client, times, "Order results, a patient on the panel", typing: false, () =>
                {
                    var (patient, order) = resulted[resultRandom.Next(resulted.Count)];
                    return $"/patients/{patient}/orders/{order}/results";
                }));
                results.Add(await MeasureAsync(client, times, "Inbox, with results to acknowledge", typing: false, () => "/inbox"));
                await AcknowledgeResultsAsync(app, clinicianId, resulted);
                // Addenda to signed notes (step 10): eight more patients join the panel, each with a signed note and
                // three addenda, one of them sensitive. They are not among the patients the chart rows pick from, so
                // those rows measure what they did. The note's page with its addenda, and the page that signs one.
                var noted = await SignNotesAsync(app, clinicianId, patients);
                var noteRandom = new Random(20261121);
                // A generator of its own, so the rows after it pick the notes they always did.
                var notedChartRandom = new Random(20261124);
                results.Add(await MeasureAsync(client, times, "Chart with a signed note, a patient on the panel", typing: false,
                    () => $"/patients/{noted[notedChartRandom.Next(noted.Count)].Patient}"));
                results.Add(await MeasureAsync(client, times, "Signed note with addenda, a patient on the panel", typing: false, () =>
                {
                    var (patient, note) = noted[noteRandom.Next(noted.Count)];
                    return $"/patients/{patient}/notes/{note}";
                }));
                results.Add(await MeasureAsync(client, times, "Note sensitivity form, a patient on the panel", typing: false, () =>
                {
                    var (patient, note) = noted[noteRandom.Next(noted.Count)];
                    return $"/patients/{patient}/notes/{note}/sensitivity";
                }));
                results.Add(await MeasureAsync(client, times, "Addendum form, a patient on the panel", typing: false, () =>
                {
                    var (patient, note) = noted[noteRandom.Next(noted.Count)];
                    return $"/patients/{patient}/notes/{note}/addendum";
                }));
                // The note editor (step 14), with its visit types and their prompts: a draft for each of those patients, in
                // four sections for a follow-up visit. A generator of its own, so the rows after it pick as they did.
                var drafted = await WriteDraftsAsync(app, clinicianId, noted);
                var draftRandom = new Random(20261126);
                results.Add(await MeasureAsync(client, times, "Note editor, a draft of a patient on the panel", typing: false, () =>
                {
                    var (patient, draft) = drafted[draftRandom.Next(drafted.Count)];
                    return $"/patients/{patient}/notes/drafts/{draft}";
                }));
                // The vital signs the editor offers for Objective when asked (step 14): the latest day's of the seeded patient.
                results.Add(await MeasureAsync(client, times, "Vital signs for a note's Objective, a patient on the panel", typing: false, () =>
                {
                    var (patient, draft) = drafted[draftRandom.Next(drafted.Count)];
                    return $"/patients/{patient}/notes/drafts/{draft}?handler=Vitals";
                }));
                // What a dictation proposes beyond the note (step 15): for each of those patients, a new draft dictated with an
                // order and an allergy to add, staged through the app's own service. Its review; the order form and the
                // allergy form opened from them; and its editor, which says they wait. A generator of its own.
                var proposed = await DictateProposalsAsync(app, clinicianId, noted);
                results.AddRange(await MeasureProposalsAsync(client, times, "", proposed));
                // A recorded visit (step 16): for each of those patients, a 20-minute conversation's review, with its transcript
                // kept apart, staged through the app's own service.
                var visits = await ListenToVisitsAsync(app, clinicianId, noted);
                results.AddRange(await MeasureVisitReviewsAsync(client, times, "", visits));
                // The patient portal's results page (UC6, FR-16; Simar Rekhi), as a patient with released results, the
                // last patient in the clinic who has some: the local stack's example account uses the first. On-site, so before the
                // off-site rows.
                var (patientUserId, patientEmail, patientPassword, portalPatient) = await CreatePatientAccountAsync(app, patients);
                var portalThread = await WritePortalThreadAsync(app, clinicianId, portalPatient);
                // Proxy access (FR-19): the clinician records the portal patient as a proxy for one of the panel.
                var proxyGrant = await RecordProxyAsync(app, clinicianId, panel[0], patientEmail);
                // Visit summaries (step 13a): six for the portal patient and six for the record they are a proxy for, two of
                // each sensitive and one withdrawn. The form that writes one, for the notes above, which have none, and
                // the one that withdraws one, for these. A generator of its own, so the rows after them pick as they did.
                var summarized = await ReleaseSummariesAsync(app, clinicianId, [portalPatient, panel[0]]);
                var summaryRandom = new Random(20261125);
                results.Add(await MeasureAsync(client, times, "Visit summary form, a patient on the panel", typing: false, () =>
                {
                    var (patient, note) = noted[summaryRandom.Next(noted.Count)];
                    return $"/patients/{patient}/notes/{note}/summary";
                }));
                results.Add(await MeasureAsync(client, times, "Withdraw summary form, a patient on the panel", typing: false, () =>
                {
                    var (patient, note, summary) = summarized[summaryRandom.Next(summarized.Count)];
                    return $"/patients/{patient}/notes/{note}/summaries/{summary}/withdraw";
                }));
                // Who viewed my record (step 13c): a year of views of each record in the audit log beyond the run's own, and a
                // panels' table of a clinic's size, which a proxy's page reads.
                await FillViewsAsync(database, clinicianId, portalPatient, panel[0]);
                await FillPanelHistoryAsync(database, clinicianId, portalPatient, panel[0]);
                // Refill requests (step 13d): the portal patient's own, and the record they are a proxy for, asked as the
                // portal asks; the first of each declined. A request's page, and the order form that approves a waiting
                // one. A generator of its own, so the rows after them pick as they did.
                var refills = await AskRefillsAsync(app, clinicianId, patientUserId, [(portalPatient, null), (panel[0], proxyGrant)]);
                results.AddRange(await MeasureRefillsAsync(client, times, "", refills));
                // Pre-visit forms (step 13e): for the portal patient, forms asked for before three appointments and all but
                // one answered. The clinician's page with them hidden and shown, and one form's answers.
                var forms = await PreVisitFormsAsync(app, clinicianId, patientUserId, portalPatient);
                results.AddRange(await MeasureFormsAsync(client, times, "", forms));
                // Kept to the end, off-site rows included: the off-site portal rows use the same session.
                var portalSession = new CookieContainer();
                // Download my record (step 13b) is measured for the seeded patient with the most entries too.
                var (largestUserId, largestEmail, largestPassword, _) = await CreatePatientAccountAsync(app, LargestRecord(patients, portalPatient));
                var largestSession = new CookieContainer();
                try
                {
                    using var portal = await PortalSignInAsync(app, portalSession, patientEmail, patientPassword);
                    results.Add(await MeasureAsync(portal, times, "Portal results, the patient's own", typing: false, () => "/portal/results"));
                    results.Add(await MeasureAsync(portal, times, "Portal appointments, the patient's own", typing: false, () => "/portal/appointments"));
                    results.Add(await MeasureAsync(portal, times, "Portal home, the patient's own", typing: false, () => "/portal"));
                    results.Add(await MeasureAsync(portal, times, "Portal messages, the patient's own", typing: false, () => "/portal/messages"));
                    results.Add(await MeasureAsync(portal, times, "Portal conversation, the patient's own", typing: false, () => $"/portal/messages/{portalThread}"));
                    results.Add(await MeasureAsync(portal, times, "Portal access, the patient's own", typing: false, () => "/portal/access"));
                    results.Add(await MeasureAsync(portal, times, "Portal choose, a record shared", typing: false, () => "/portal/choose"));
                    results.Add(await MeasureAsync(portal, times, "Portal visit summaries, the patient's own", typing: false, () => "/portal/summaries"));
                    results.Add(await MeasureAsync(portal, times, "Portal refills, the patient's own", typing: false, () => "/portal/refills"));
                    // Pre-visit forms (step 13e): the list, and the GAD-7 still open.
                    results.AddRange(await MeasurePortalFormsAsync(portal, times, "", forms));
                    // Download my record (step 13b): the page, the FHIR file and the printable page.
                    results.AddRange(await MeasureRecordAsync(portal, times, "", "the patient's own", page: true));
                    results.Add(await MeasureAsync(portal, times, "Portal who viewed, the patient's own", typing: false, () => "/portal/views"));
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
                    results.Add(await MeasureAsync(portal, times, "Portal visit summaries, as a proxy", typing: false, () => "/portal/summaries"));
                    results.Add(await MeasureAsync(portal, times, "Portal refills, as a proxy", typing: false, () => "/portal/refills"));
                    // A proxy without sensitive items: the record has no forms for them, as the database decides.
                    results.Add(await MeasureAsync(portal, times, "Portal pre-visit forms, as a proxy", typing: false, () => "/portal/forms"));
                    results.AddRange(await MeasureRecordAsync(portal, times, "", "as a proxy", page: false));
                    // And for the seeded patient whose record has the most entries, with a session of its own.
                    using (var largest = await PortalSignInAsync(app, largestSession, largestEmail, largestPassword))
                    {
                        results.AddRange(await MeasureRecordAsync(largest, times, "", "the largest seeded record", page: false));
                    }
                    results.Add(await MeasureAsync(portal, times, "Portal who viewed, as a proxy", typing: false, () => "/portal/views"));

                    if (database is null)
                    {
                        Console.WriteLine("Off-site: not measured with --stack, which has no ehr_read login for the replica.");
                        Console.WriteLine("The lab's door: not measured with --stack, whose lab key is in the stack's .env alone.");
                    }
                    else
                    {
                        await SettleAsync(database);
                        results.AddRange(await MeasureOffSiteAsync(app, database, session, panel, labOrders, patients, scheduleDay, appointments, noted, summarized, portalSession, largestSession,
                            refills, forms, drafted, proposed, visits));
                        // The lab's door (step 12), last, so every row before it makes the requests it always did.
                        results.AddRange(await MeasureLabAsync(app, database, times, clinicianId, panel));
                    }
                }
                finally
                {
                    await LockOutPatientAsync(app, patientUserId);
                    await LockOutPatientAsync(app, largestUserId);
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
        // The panel's seeded sets of vital signs (step 9b): the patient and the time each was taken. A generator of
        // its own, so the rows after this one pick the patients they always did.
        var step = patients / Math.Min(PanelSize, patients);
        var sets = Enumerable.Range(0, panel.Count).SelectMany(i => SyntheticClinic.Chart(i * step).Vitals.Select(v => (Patient: panel[i], At: v.EffectiveAt.UtcDateTime)))
            .Distinct().Take(200).ToList();
        var setRandom = new Random(20261122);

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
            // Vital signs (step 9): the flowsheet and its trend charts, drawn by the server.
            await MeasureAsync(client, times, prefix + "Vital signs, a patient on the panel", typing: false, () => $"/patients/{panel[random.Next(panel.Count)]}/vitals"),
            // Step 16: listening to a visit, its consent first; off-site, the page that says it works only at the clinic.
            await MeasureAsync(client, times, prefix + "Listen to the visit, a patient on the panel", typing: false, () => $"/patients/{panel[random.Next(panel.Count)]}/listen"),
            // Step 9b: the page that marks a set's readings entered in error, for a seeded visit's set.
            await MeasureAsync(client, times, prefix + "Mark vital signs in error form, a patient on the panel", typing: false, () =>
            {
                var (patient, at) = sets[setRandom.Next(sets.Count)];
                return $"/patients/{patient}/vitals/error?at={at:yyyy-MM-ddTHH:mm:ss}";
            }),
            await MeasureAsync(client, times, prefix + "Chart history, a patient on the panel", typing: false,
                () => $"/patients/{panel[random.Next(panel.Count)]}/chart-history"),
            .. await MeasureFhirAsync(client, times, prefix, panel),
        ];
    }

    // The FHIR API (the expansion plan's step 11, UC 19), last, with generators of its own, so the rows above make the
    // requests they always did: a patient; their results and vital signs, the API's largest answer; their diagnoses
    // with the sensitive ones asked for, a read of sensitive items; and a blood pressure reading by its ID, which asks
    // whose it is before the gate and reads the reading's two values together. The searches and the reads go to the
    // panel's patients who have a blood pressure reading, so the readings are always among what is measured; their
    // readings' IDs come from the API itself, once, before the rows.
    static async Task<List<Result>> MeasureFhirAsync(HttpClient client, DatabaseTimes times, string prefix, List<Guid> panel)
    {
        var measured = new List<Guid>();
        var readings = new List<Guid>();
        foreach (var patient in panel)
        {
            using var found = JsonDocument.Parse(await client.GetStringAsync($"/fhir/Observation?patient={patient}&category=vital-signs"));
            var pressures = !found.RootElement.TryGetProperty("entry", out var entries)
                ? []
                : entries.EnumerateArray().Select(entry => entry.GetProperty("resource"))
                    .Where(resource => resource.GetProperty("code").GetProperty("coding")[0].GetProperty("code").GetString() == "85354-9")
                    .Select(resource => Guid.Parse(resource.GetProperty("id").GetString()!)).ToList();
            if (pressures.Count > 0)
            {
                measured.Add(patient);
                readings.AddRange(pressures);
            }
        }

        if (measured.Count < panel.Count / 2)
        {
            throw new InvalidOperationException($"Only {measured.Count} of the panel's {panel.Count} patients have a blood pressure reading to measure.");
        }

        var patients = new Random(20261125);
        var searched = new Random(20261126);
        var sensitive = new Random(20261127);
        var byId = new Random(20261128);
        return
        [
            await MeasureAsync(client, times, prefix + "FHIR patient, a patient on the panel", typing: false, () => $"/fhir/Patient/{measured[patients.Next(measured.Count)]}"),
            await MeasureAsync(client, times, prefix + "FHIR observations, a patient on the panel", typing: false,
                () => $"/fhir/Observation?patient={measured[searched.Next(measured.Count)]}"),
            await MeasureAsync(client, times, prefix + "FHIR conditions with sensitive items, a patient on the panel", typing: false,
                () => $"/fhir/Condition?patient={measured[sensitive.Next(measured.Count)]}&show-sensitive=true"),
            await MeasureAsync(client, times, prefix + "FHIR observation by ID, a patient on the panel", typing: false,
                () => $"/fhir/Observation/{readings[byId.Next(readings.Count)]}"),
        ];
    }

    // What a clinician might have typed so far when looking for a diagnosis.
    static readonly string[] CodeSearches = ["typ", "ess", "anx", "ast", "hyp", "dia", "dep", "alc", "low", "mig"];

    static readonly string[] CodeWords = ["diabetes", "mellitus compl", "fracture femur", "pneumonia", "kidney chronic", "pain", "infarction", "left", "unspecified site", "encounter"];

    // UC 2 (Tahseen Arnan): the same requests to the cloud app, with the on-site session's cookies, during an outage. Its
    // audit queue goes in a folder of its own, deleted at the end.
    static async Task<List<Result>> MeasureOffSiteAsync(
        BenchApp onPrem, MigratedDatabase database, CookieContainer session, List<Guid> panel, IReadOnlyDictionary<Guid, Guid> labOrders, int patients,
        string scheduleDay, List<Guid> appointments, List<(Guid Patient, Guid Note)> noted, List<(Guid Patient, Guid Note, Guid Summary)> summarized,
        CookieContainer portalSession, CookieContainer largestSession, List<(Guid Patient, Guid Request, bool Waiting)> refills, FormRows forms,
        List<(Guid Patient, Guid Draft)> drafted, List<Proposed> proposed, List<Listened> visits)
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
            // Pages that record more than one audit event (step 10c): a chart that has a signed note records the
            // patient's read and the note's; a note's page records the patient's and the note's. Off-site each event
            // used to be its own write to the queue, flushed to disk; now a request's events go together.
            var noteRandom = new Random(20261123);
            results.Add(await MeasureAsync(client, times, "Off-site, Chart with a signed note, a patient on the panel", typing: false,
                () => $"/patients/{noted[noteRandom.Next(noted.Count)].Patient}"));
            results.Add(await MeasureAsync(client, times, "Off-site, Signed note with addenda, a patient on the panel", typing: false, () =>
            {
                var (patient, note) = noted[noteRandom.Next(noted.Count)];
                return $"/patients/{patient}/notes/{note}";
            }));
            // Visit summaries (step 13a), with the same generator's draws as on-site: the clinician's two forms (which,
            // during an outage, say why nothing can be released or withdrawn), then the portal page, first as the proxy
            // the on-site rows ended as, then back on the patient's own record, through the cloud app's own switch.
            var summaryRandom = new Random(20261125);
            results.Add(await MeasureAsync(client, times, "Off-site, Visit summary form, a patient on the panel", typing: false, () =>
            {
                var (patient, note) = noted[summaryRandom.Next(noted.Count)];
                return $"/patients/{patient}/notes/{note}/summary";
            }));
            results.Add(await MeasureAsync(client, times, "Off-site, Withdraw summary form, a patient on the panel", typing: false, () =>
            {
                var (patient, note, summary) = summarized[summaryRandom.Next(summarized.Count)];
                return $"/patients/{patient}/notes/{note}/summaries/{summary}/withdraw";
            }));
            // The note editor (step 14), with the same generator's draws as on-site: during an outage, the draft as last
            // saved, read-only; and the vital signs it offers for Objective, read from the replica.
            var draftRandom = new Random(20261126);
            results.Add(await MeasureAsync(client, times, "Off-site, Note editor, a draft of a patient on the panel", typing: false, () =>
            {
                var (patient, draft) = drafted[draftRandom.Next(drafted.Count)];
                return $"/patients/{patient}/notes/drafts/{draft}";
            }));
            results.Add(await MeasureAsync(client, times, "Off-site, Vital signs for a note's Objective, a patient on the panel", typing: false, () =>
            {
                var (patient, draft) = drafted[draftRandom.Next(drafted.Count)];
                return $"/patients/{patient}/notes/drafts/{draft}?handler=Vitals";
            }));
            results.AddRange(await MeasureRefillsAsync(client, times, "Off-site, ", refills));
            // A dictation's proposals (step 15), read from the cloud copy: opening or dismissing one says why it can't.
            results.AddRange(await MeasureProposalsAsync(client, times, "Off-site, ", proposed));
            // A recorded visit's review (step 16), read from the cloud copy: without its transcript, which is read at the clinic.
            results.AddRange(await MeasureVisitReviewsAsync(client, times, "Off-site, ", visits));
            // Pre-visit forms (step 13e): the clinician's pages, read from the cloud copy; asking says why it can't.
            results.AddRange(await MeasureFormsAsync(client, times, "Off-site, ", forms));
            using var portal = cloud.CreateDefaultClient(Base, new RedirectHandler(), new CookieContainerHandler(portalSession));
            results.Add(await MeasureAsync(portal, times, "Off-site, Portal visit summaries, as a proxy", typing: false, () => "/portal/summaries"));
            results.Add(await MeasureAsync(portal, times, "Off-site, Portal refills, as a proxy", typing: false, () => "/portal/refills"));
            results.Add(await MeasureAsync(portal, times, "Off-site, Portal pre-visit forms, as a proxy", typing: false, () => "/portal/forms"));
            results.AddRange(await MeasureRecordAsync(portal, times, "Off-site, ", "as a proxy", page: false));
            results.Add(await MeasureAsync(portal, times, "Off-site, Portal who viewed, as a proxy", typing: false, () => "/portal/views"));
            using (var mine = await PostFormAsync(portal, "/portal/acting?handler=Mine", await portal.GetStringAsync("/portal/summaries")))
            {
                if (mine.RequestMessage?.RequestUri?.AbsolutePath != "/portal")
                {
                    throw new InvalidOperationException("Going back to the patient's own record off-site did not end at /portal.");
                }
            }

            results.Add(await MeasureAsync(portal, times, "Off-site, Portal visit summaries, the patient's own", typing: false, () => "/portal/summaries"));
            results.Add(await MeasureAsync(portal, times, "Off-site, Portal refills, the patient's own", typing: false, () => "/portal/refills"));
            results.AddRange(await MeasurePortalFormsAsync(portal, times, "Off-site, ", forms));
            results.AddRange(await MeasureRecordAsync(portal, times, "Off-site, ", "the patient's own", page: true));
            using var largest = cloud.CreateDefaultClient(Base, new RedirectHandler(), new CookieContainerHandler(largestSession));
            results.AddRange(await MeasureRecordAsync(largest, times, "Off-site, ", "the largest seeded record", page: false));
            results.Add(await MeasureAsync(portal, times, "Off-site, Portal who viewed, the patient's own", typing: false, () => "/portal/views"));
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

    // The lab's door (step 12, Api/Lab), on-prem, with the lab's key, as a lab's system calls it. A lab asking for what
    // was signed since it last asked: ten lab orders signed together for ten of the panel, each ask handing all ten over,
    // with an audit event for each. Then a lab filing one order's results, a complete blood count's four, through the
    // same rules as a clinician's filing (the order resulted, its signer told), each request for another of 1,100
    // orders signed for the panel beforehand. Each answer must be the door's success: 200, then 204.
    static async Task<List<Result>> MeasureLabAsync(BenchApp app, MigratedDatabase database, DatabaseTimes times, Guid clinicianId, List<Guid> panel)
    {
        using var lab = app.CreateDefaultClient(Base);
        lab.DefaultRequestHeaders.Add("X-Lab-Key", MigratedDatabase.LabKey);
        string since;
        await using (var connection = new NpgsqlConnection(database.SuperuserConnectionString))
        {
            await connection.OpenAsync();
            await using var watermark = new NpgsqlCommand("SELECT pg_snapshot_xmin(pg_current_snapshot())::text", connection);
            since = (string)(await watermark.ExecuteScalarAsync())!;
        }

        await SignLabOrdersAsync(app, clinicianId, panel.Take(10));
        var asked = await MeasureLabRequestsAsync(lab, times, "Lab orders, ten waiting since the last ask", HttpStatusCode.OK,
            _ => new HttpRequestMessage(HttpMethod.Get, $"/api/lab/orders?since={since}"));

        var orders = await SignLabOrdersAsync(app, clinicianId, Enumerable.Range(0, WarmUp + Measured).Select(i => panel[i % panel.Count]));
        var collected = DateTimeOffset.UtcNow.AddMinutes(-10);
        var results = new[] { ("718-7", 13.5m, "g/dL"), ("4544-3", 41m, "%"), ("6690-2", 6.2m, "10*3/uL"), ("777-3", 250m, "10*3/uL") }
            .Select(result => new { code = result.Item1, value = result.Item2, unit = result.Item3 }).ToArray();
        var filed = await MeasureLabRequestsAsync(lab, times, "Lab results, one order filed", HttpStatusCode.NoContent,
            i => new HttpRequestMessage(HttpMethod.Post, $"/api/lab/orders/{orders[i]}/results") { Content = JsonContent.Create(new { collectedAt = collected, results }) });
        return [asked, filed];
    }

    // Complete blood counts the clinician signs, one for each patient given, in one transaction, with a reason to order
    // one again for a patient who has one active: their IDs, in that order.
    static async Task<List<Guid>> SignLabOrdersAsync(BenchApp app, Guid clinicianId, IEnumerable<Guid> patients)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<UserContext>().UserId = clinicianId;
        var db = scope.ServiceProvider.GetRequiredService<EhrDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var orders = patients.Select(patient => new Order
        {
            Id = Guid.CreateVersion7(),
            PatientId = patient,
            Type = OrderType.Lab,
            CatalogCode = "SYN-LAB-001",
            Details = "{}",
            Status = OrderStatus.Active,
            DuplicateReason = "Synthetic: benchmark repeat",
        }).ToList();
        db.AddRange(orders);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return [.. orders.Select(order => order.Id)];
    }

    // As MeasureAsync, for the lab's door: each request made by `next` from its number, each answer the status expected.
    static async Task<Result> MeasureLabRequestsAsync(HttpClient client, DatabaseTimes times, string name, HttpStatusCode expected, Func<int, HttpRequestMessage> next)
    {
        var total = new double[Measured];
        var audit = new double[Measured];
        var commit = new double[Measured];
        var row = Stopwatch.StartNew();
        for (var i = 0; i < WarmUp + Measured; i++)
        {
            if (row.Elapsed > RowLimit)
            {
                throw new InvalidOperationException(
                    $"{name}: stopped after {i:N0} of {WarmUp + Measured:N0} requests, which took over {RowLimit.TotalSeconds:0} seconds; at {BudgetMilliseconds} ms each, all of them take {(WarmUp + Measured) * BudgetMilliseconds / 1000:0} seconds.");
            }

            using var request = next(i);
            times.Reset();
            var started = Stopwatch.GetTimestamp();
            using var response = await client.SendAsync(request);
            await response.Content.ReadAsByteArrayAsync();
            var elapsed = Stopwatch.GetElapsedTime(started);
            if (response.StatusCode != expected)
            {
                throw new InvalidOperationException($"{name}: {request.Method} {request.RequestUri} answered {(int)response.StatusCode}.");
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

    // Times one kind of request, after warming it up, and its audit events' insert and its commit within it. Every
    // answer must be the page itself, and all of them must come within RowLimit.
    static async Task<Result> MeasureAsync(HttpClient client, DatabaseTimes times, string name, bool typing, Func<string> next)
    {
        var addresses = Enumerable.Range(0, WarmUp + Measured).Select(_ => next()).ToList();
        var total = new double[Measured];
        var audit = new double[Measured];
        var commit = new double[Measured];
        var row = Stopwatch.StartNew();
        for (var i = 0; i < addresses.Count; i++)
        {
            if (row.Elapsed > RowLimit)
            {
                throw new InvalidOperationException(
                    $"{name}: stopped after {i:N0} of {addresses.Count:N0} requests, which took over {RowLimit.TotalSeconds:0} seconds; at {BudgetMilliseconds} ms each, all of them take {addresses.Count * BudgetMilliseconds / 1000:0} seconds.");
            }

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

    // Download my record (step 13b), as the session's record: its page (when asked), the FHIR file and the printable page.
    static async Task<List<Result>> MeasureRecordAsync(HttpClient portal, DatabaseTimes times, string site, string whose, bool page)
    {
        var results = new List<Result>();
        if (page)
        {
            results.Add(await MeasureAsync(portal, times, $"{site}Portal record page, {whose}", typing: false, () => "/portal/record"));
        }

        results.Add(await MeasureAsync(portal, times, $"{site}Portal record file, {whose}", typing: false, () => "/portal/record?handler=File"));
        results.Add(await MeasureAsync(portal, times, $"{site}Portal printable record, {whose}", typing: false, () => "/portal/record/print"));
        return results;
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
    // page's events together and flushes them to disk before the page is sent (RequestAuditEvents, step 10c, and
    // CloudAuditQueue), as an on-prem page's commit waits
    // for its audit events to reach the disk; writing a few lines of JSON is a small part of it.
    internal sealed class TimedAuditQueue(IDurableAuditQueue queue, DatabaseTimes times) : IDurableAuditQueue
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
    static Task<(Guid Id, string Email, string Password, Guid Patient)> CreatePatientAccountAsync(BenchApp app, int patients) =>
        CreatePatientAccountAsync(app, Enumerable.Range(0, patients).Reverse()
            .Select(SyntheticClinic.Chart)
            .First(chart => chart.Results.Count(r => r.IssuedAt is not null) >= 3)
            .Patient.Id);

    // The seeded patient whose record has the most entries the download holds, besides the one given, who has an account
    // already: the first of them in the clinic's order.
    static Guid LargestRecord(int patients, Guid besides) => Enumerable.Range(0, patients)
        .Select(SyntheticClinic.Chart)
        .Where(chart => chart.Patient.Id != besides)
        .MaxBy(chart => chart.Allergies.Count + chart.Medications.Count + chart.Conditions.Count + chart.Results.Count + chart.Vitals.Count)!
        .Patient.Id;

    // A confirmed portal account, with a random password nobody keeps, for this patient.
    static async Task<(Guid Id, string Email, string Password, Guid Patient)> CreatePatientAccountAsync(BenchApp app, Guid patient)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PatientUser>>();
        var email = $"bench-patient-{Guid.NewGuid():N}@example.test";
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var user = new PatientUser { UserName = email, Email = email, EmailConfirmed = true, PatientId = patient };
        Check(await users.CreateAsync(user, password));
        return (user.Id, email, password, patient);
    }

    // Signs a patient in at the portal, as a browser does, and returns a client with the session's cookies, which are kept
    // in `session` for the off-site rows.
    static async Task<HttpClient> PortalSignInAsync(BenchApp app, CookieContainer session, string email, string password)
    {
        var client = app.CreateDefaultClient(Base, new RedirectHandler(), new CookieContainerHandler(session));
        using var home = await PostFormAsync(client, "/portal/sign-in", await client.GetStringAsync("/portal/sign-in"), ("Input.Email", email), ("Input.Password", password));
        // An account with a record shared with it lands on the page that asks whose record (step 6b).
        if (home.RequestMessage?.RequestUri?.AbsolutePath is not ("/portal" or "/portal/choose"))
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
                    // The seeded patients take medications of their own, some of which interact with what is ordered
                    // here (step 8): the database then signs the order only with a reason, and drops one it doesn't need.
                    SafetyOverrideReason = item.Type == OrderType.Medication ? "Synthetic: benchmark order" : null,
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

    // A lipid panel signed and resulted for each of the first 24 patients on the panel, through the app's own service
    // and the database's rules: total cholesterol inside its range for half and above it for the rest, and HDL.
    static async Task<List<(Guid Patient, Guid Order)>> FileResultsAsync(BenchApp app, Guid clinicianId, List<Guid> panel)
    {
        var resulted = new List<(Guid, Guid)>();
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<UserContext>().UserId = clinicianId;
        var db = scope.ServiceProvider.GetRequiredService<EhrDbContext>();
        var lab = scope.ServiceProvider.GetRequiredService<Ehr.Domain.Results.LabResults>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        foreach (var (patient, index) in panel.Take(DaySize).Select((patient, index) => (patient, index)))
        {
            var order = new Order { Id = Guid.CreateVersion7(), PatientId = patient, Type = OrderType.Lab, CatalogCode = "SYN-LAB-004", Details = "{}", Status = OrderStatus.Active };
            db.Add(order);
            await db.SaveChangesAsync();
            if (!await lab.FileAsync(patient, order.Id, [("2093-3", index % 2 == 0 ? 180m : 260m), ("2085-9", 55m)], DateTimeOffset.UtcNow.AddMinutes(-30), CancellationToken.None))
            {
                throw new InvalidOperationException("The benchmark's results were refused.");
            }

            resulted.Add((patient, order.Id));
        }

        await transaction.CommitAsync();
        return resulted;
    }

    // Step 10: a signed note with three addenda (one sensitive) for each of eight patients added to the panel for it,
    // through the database's own signing triggers and policies.
    static async Task<List<(Guid Patient, Guid Note)>> SignNotesAsync(BenchApp app, Guid clinicianId, int patients)
    {
        var noted = new List<(Guid, Guid)>();
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<UserContext>().UserId = clinicianId;
        var db = scope.ServiceProvider.GetRequiredService<EhrDbContext>();
        var assignments = scope.ServiceProvider.GetRequiredService<PatientAssignments>();
        var notes = scope.ServiceProvider.GetRequiredService<Ehr.Domain.Notes.ClinicalNotes>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var step = patients / Math.Min(PanelSize, patients);
        foreach (var index in Enumerable.Range(0, 8).Select(i => ((i * step) + (step / 2)) % patients).Distinct())
        {
            var patient = SyntheticClinic.Chart(index).Patient.Id;
            await assignments.AssignAsync(patient, CancellationToken.None);
            var draft = new Ehr.Data.Patients.ClinicalDraft
            {
                Id = Guid.CreateVersion7(),
                PatientId = patient,
                ClinicianId = clinicianId,
                Body = "Synthetic note",
                Version = 1,
                Subjective = "Synthetic subjective: cough for two days.",
                Objective = "Synthetic objective: chest clear.",
                Assessment = "Synthetic assessment: viral illness.",
                Plan = "Synthetic plan: rest and follow-up.",
                StructuredVersion = 1,
            };
            db.Add(draft);
            await db.SaveChangesAsync();
            var note = new Ehr.Data.Patients.ClinicalNote
            {
                Id = Guid.CreateVersion7(),
                DraftId = draft.Id,
                PatientId = patient,
                DraftVersion = 1,
                Subjective = draft.Subjective,
                Objective = draft.Objective,
                Assessment = draft.Assessment,
                Plan = draft.Plan,
            };
            db.Add(note);
            await db.SaveChangesAsync();
            foreach (var (text, sensitive) in new[] { ("Synthetic addendum: culture negative.", false), ("Synthetic addendum: seen in follow-up, improving.", false), ("Synthetic sensitive addendum.", true) })
            {
                if ((await notes.AddAddendumAsync(patient, note.Id, text, sensitive, CancellationToken.None)).Status != 200)
                {
                    throw new InvalidOperationException("The benchmark's addendum was refused.");
                }
            }

            noted.Add((patient, note.Id));
        }

        await transaction.CommitAsync();
        return noted;
    }

    // Step 14: a draft for each patient with a signed note, written in four sections for a follow-up visit, through the
    // app's own service and the database's rules.
    static async Task<List<(Guid Patient, Guid Draft)>> WriteDraftsAsync(BenchApp app, Guid clinicianId, List<(Guid Patient, Guid Note)> noted)
    {
        var drafted = new List<(Guid, Guid)>();
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<UserContext>().UserId = clinicianId;
        var db = scope.ServiceProvider.GetRequiredService<EhrDbContext>();
        var notes = scope.ServiceProvider.GetRequiredService<Ehr.Domain.Notes.ClinicalNotes>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        foreach (var (patient, _) in noted)
        {
            var draft = await notes.CreateAsync(patient, CancellationToken.None) ?? throw new InvalidOperationException("The benchmark's draft was refused.");
            var content = new Ehr.Domain.Notes.NoteContent("Synthetic subjective: no new symptoms.", "Synthetic objective: blood pressure 128/78.",
                "Synthetic assessment: hypertension controlled.", "Synthetic plan: same dose, review in three months.");
            if ((await notes.SaveAsync(patient, draft.Id, draft.Version, content, CancellationToken.None, "follow-up")).Status != 200)
            {
                throw new InvalidOperationException("The benchmark's draft was not saved.");
            }

            drafted.Add((patient, draft.Id));
        }

        await transaction.CommitAsync();
        return drafted;
    }

    // A dictation of a new draft for a patient, and its two proposals (step 15).
    sealed record Proposed(Guid Patient, Guid Draft, Guid Review, Guid Order, Guid Allergy);

    // Step 15: for each patient, a new draft dictated with an order and an allergy to add, begun and staged through the app's
    // own service and the database's rules, as the dictation page does after Stop. The result is the one the service would
    // give: its transcript, no field, the two instructions it lists, and the two it proposes.
    static async Task<List<Proposed>> DictateProposalsAsync(BenchApp app, Guid clinicianId, List<(Guid Patient, Guid Note)> noted)
    {
        const string text = "Synthetic: blood pressure is fine today. Go ahead and order a lipid panel. Add penicillin to her allergy list.";
        const string order = "Go ahead and order a lipid panel", allergy = "Add penicillin to her allergy list";
        var (orderAt, allergyAt) = (text.IndexOf(order, StringComparison.Ordinal), text.IndexOf(allergy, StringComparison.Ordinal));
        var empty = new Ehr.Web.Api.Dictation.EmptyField(["not_dictated"]);
        var result = new Ehr.Web.Api.Dictation.DictationResult(
            new Ehr.Web.Api.Dictation.Transcript(text, true, [new Ehr.Web.Api.Dictation.TranscriptSegment(0, text.Length, 0, 9000, true)]),
            Ehr.Web.Api.Dictation.Sorting.Done, new Ehr.Web.Api.Dictation.NoteFields(empty, empty, empty, empty),
            [new(Ehr.Web.Api.Dictation.Instruction.Order, orderAt, orderAt + order.Length),
                new(Ehr.Web.Api.Dictation.Instruction.AllergyChange, allergyAt, allergyAt + allergy.Length)],
            new Ehr.Web.Api.Dictation.Provenance(new("synthetic", "0", "0"), null, null, "synthetic", "synthetic", "synthetic", "synthetic"),
            Proposals:
            [
                new(Ehr.Web.Api.Dictation.ProposalKind.Order, orderAt, orderAt + order.Length, []),
                new(Ehr.Web.Api.Dictation.ProposalKind.AllergyAddition, allergyAt, allergyAt + allergy.Length, []),
            ]);
        var trusted = new Ehr.Domain.Notes.TrustedReviewResult(JsonSerializer.Serialize(result, JsonSerializerOptions.Web), new Dictionary<string, string>(),
            [AiChartProposalKinds.Order, AiChartProposalKinds.AllergyAddition]);

        var proposed = new List<Proposed>();
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<UserContext>().UserId = clinicianId;
        var db = scope.ServiceProvider.GetRequiredService<EhrDbContext>();
        var reviews = scope.ServiceProvider.GetRequiredService<Ehr.Domain.Notes.NoteReviews>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        foreach (var (patient, _) in noted)
        {
            var begun = await reviews.BeginAsync(Guid.NewGuid(), patient, null, null, Guid.NewGuid().ToString("N"), 180, 1024 * 1024, CancellationToken.None);
            if (begun.Status != 201 || begun.Review is not { } review)
            {
                throw new InvalidOperationException("The benchmark's dictation was refused.");
            }

            if ((await reviews.StageAsync(review.Id, begun.Version!.Value, trusted, CancellationToken.None)).Status != 200)
            {
                throw new InvalidOperationException("The benchmark's dictation was not staged.");
            }

            var rows = await db.AiChartProposals.AsNoTracking().Where(p => p.ReviewId == review.Id).OrderBy(p => p.Ordinal).Select(p => p.Id).ToListAsync();
            proposed.Add(new(patient, review.DraftId, review.Id, rows[0], rows[1]));
        }

        await transaction.CommitAsync();
        return proposed;
    }

    // A recorded visit's review (step 16).
    sealed record Listened(Guid Patient, Guid Draft, Guid Review);

    // Step 16: for each patient, a new draft from a 20-minute recorded visit, begun with its consent and staged through the
    // app's own service and the database's rules, as the Listen page does: 3,000 words or so (150 a minute), kept apart,
    // and a value proposed for each of the four fields from who said it. The dictation service cuts its transcript into a
    // segment at the first pause of at least 0.4 seconds after at least a second (segmenter.py), and a conversation pauses
    // that long every couple of seconds, between phrases and at each turn: 20 minutes is about 600 segments of 2 seconds,
    // here in 200 turns of three, five words each.
    static async Task<List<Listened>> ListenToVisitsAsync(BenchApp app, Guid clinicianId, List<(Guid Patient, Guid Note)> noted)
    {
        var text = new StringBuilder();
        var segments = new List<Ehr.Web.Api.Dictation.TranscriptSegment>();
        for (var passage = 0; passage < 600; passage++)
        {
            var turn = passage / 3;
            var (speaker, words) = turn % 2 == 0
                ? (Ehr.Web.Api.Dictation.Speaker.Clinician, $"Synthetic question {turn}, part {passage % 3}.")
                : (Ehr.Web.Api.Dictation.Speaker.Patient, $"Synthetic answer {turn}, part {passage % 3}.");
            text.Append(passage == 0 ? "" : passage % 3 == 0 ? "\n" : " ");
            segments.Add(new(text.Length, text.Length + words.Length, passage * 2000L, passage * 2000L + 1800, true, null, 1 + turn % 2, speaker));
            text.Append(words);
        }

        // From the patient's first passage, and the clinician's.
        Ehr.Web.Api.Dictation.PendingField Field(int passage) => new(
            text.ToString(segments[passage].Start, segments[passage].End - segments[passage].Start),
            [new(segments[passage].Start, segments[passage].End, segments[passage].Speaker)], []);
        var fields = new Ehr.Web.Api.Dictation.NoteFields(Field(3), Field(0), Field(6), Field(12));
        var transcript = new Ehr.Web.Api.Dictation.Transcript(text.ToString(), true, segments);
        var result = new Ehr.Web.Api.Dictation.DictationResult(transcript with { Text = "" }, Ehr.Web.Api.Dictation.Sorting.Done, fields, [],
            new Ehr.Web.Api.Dictation.Provenance(new("synthetic", "0", "0"), null, null, "synthetic", "synthetic", "synthetic", "synthetic"));
        var trusted = new Ehr.Domain.Notes.TrustedReviewResult(JsonSerializer.Serialize(result, JsonSerializerOptions.Web),
            new Dictionary<string, string>
            {
                ["subjective"] = ((Ehr.Web.Api.Dictation.PendingField)fields.Subjective).Value,
                ["objective"] = ((Ehr.Web.Api.Dictation.PendingField)fields.Objective).Value,
                ["assessment"] = ((Ehr.Web.Api.Dictation.PendingField)fields.Assessment).Value,
                ["plan"] = ((Ehr.Web.Api.Dictation.PendingField)fields.Plan).Value,
            },
            [], JsonSerializer.Serialize(transcript, JsonSerializerOptions.Web));

        var listened = new List<Listened>();
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<UserContext>().UserId = clinicianId;
        var db = scope.ServiceProvider.GetRequiredService<EhrDbContext>();
        var reviews = scope.ServiceProvider.GetRequiredService<Ehr.Domain.Notes.NoteReviews>();
        await using var dbTransaction = await db.Database.BeginTransactionAsync();
        foreach (var (patient, _) in noted)
        {
            var begun = await reviews.BeginAsync(Guid.NewGuid(), patient, null, null, Guid.NewGuid().ToString("N"), 1200, 1024 * 1024,
                CancellationToken.None, new Ehr.Domain.Notes.AmbientStart(2, AmbientConsentGivers.Patient));
            if (begun.Status != 201 || begun.Review is not { } review
                || (await reviews.StageAsync(review.Id, begun.Version!.Value, trusted, CancellationToken.None)).Status != 200)
            {
                throw new InvalidOperationException("The benchmark's recorded visit was refused.");
            }

            listened.Add(new(patient, review.DraftId, review.Id));
        }

        await dbTransaction.CommitAsync();
        return listened;
    }

    static async Task<List<Result>> MeasureVisitReviewsAsync(HttpClient client, DatabaseTimes times, string site, List<Listened> visits)
    {
        var random = new Random(20261128);
        return
        [
            await MeasureAsync(client, times, $"{site}Note review of a 20-minute recorded visit, a patient on the panel", typing: false, () =>
            {
                var one = visits[random.Next(visits.Count)];
                return $"/patients/{one.Patient}/notes/drafts/{one.Draft}/review/{one.Review}";
            }),
        ];
    }

    static async Task<List<Result>> MeasureProposalsAsync(HttpClient client, DatabaseTimes times, string site, List<Proposed> proposed)
    {
        var random = new Random(20261127);
        return
        [
            await MeasureAsync(client, times, $"{site}Note review with dictation proposals, a patient on the panel", typing: false, () =>
            {
                var one = proposed[random.Next(proposed.Count)];
                return $"/patients/{one.Patient}/notes/drafts/{one.Draft}/review/{one.Review}";
            }),
            await MeasureAsync(client, times, $"{site}Order form from a dictation proposal, a patient on the panel", typing: false, () =>
            {
                var one = proposed[random.Next(proposed.Count)];
                return $"/patients/{one.Patient}/orders/new?type=lab&proposal={one.Order}";
            }),
            await MeasureAsync(client, times, $"{site}Allergy form from a dictation proposal, a patient on the panel", typing: false, () =>
            {
                var one = proposed[random.Next(proposed.Count)];
                return $"/patients/{one.Patient}/allergies?proposal={one.Allergy}";
            }),
            await MeasureAsync(client, times, $"{site}Note editor with dictation proposals waiting, a patient on the panel", typing: false, () =>
            {
                var one = proposed[random.Next(proposed.Count)];
                return $"/patients/{one.Patient}/notes/drafts/{one.Draft}";
            }),
        ];
    }

    // Step 13a: for each patient, six signed notes and a visit summary of each, through the app's own service and the
    // database's rules: two ticked sensitive, and one withdrawn and released again, so the portal has a placeholder. The
    // patients are on the clinician's panel. The standing summaries.
    static async Task<List<(Guid Patient, Guid Note, Guid Summary)>> ReleaseSummariesAsync(BenchApp app, Guid clinicianId, Guid[] patients)
    {
        var standing = new List<(Guid, Guid, Guid)>();
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<UserContext>().UserId = clinicianId;
        var db = scope.ServiceProvider.GetRequiredService<EhrDbContext>();
        var summaries = scope.ServiceProvider.GetRequiredService<Ehr.Domain.Notes.VisitSummaries>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        foreach (var patient in patients)
        {
            for (var i = 0; i < 6; i++)
            {
                var draft = new Ehr.Data.Patients.ClinicalDraft
                {
                    Id = Guid.CreateVersion7(),
                    PatientId = patient,
                    ClinicianId = clinicianId,
                    Body = "Synthetic note",
                    Version = 1,
                    Subjective = "Synthetic subjective: a check-up.",
                    Objective = "Synthetic objective: nothing abnormal found.",
                    Assessment = "Synthetic assessment: well.",
                    Plan = "Synthetic plan: see you in a year.",
                    StructuredVersion = 1,
                };
                db.Add(draft);
                await db.SaveChangesAsync();
                var note = new Ehr.Data.Patients.ClinicalNote
                {
                    Id = Guid.CreateVersion7(),
                    DraftId = draft.Id,
                    PatientId = patient,
                    DraftVersion = 1,
                    Subjective = draft.Subjective,
                    Objective = draft.Objective,
                    Assessment = draft.Assessment,
                    Plan = draft.Plan,
                };
                db.Add(note);
                await db.SaveChangesAsync();
                async Task<Guid> ReleaseAsync()
                {
                    var released = await summaries.ReleaseAsync(patient, note.Id, "We talked about how you have been.", "Everything we checked was as expected.\nYour weight is steady.",
                        "Keep doing what you are doing, and come back in a year.", sensitive: i % 3 == 2, CancellationToken.None);
                    return released.Summary ?? throw new InvalidOperationException("The benchmark's visit summary was refused: " + string.Join(" ", released.Errors));
                }

                var summary = await ReleaseAsync();
                if (i == 0)
                {
                    if ((await summaries.WithdrawAsync(patient, summary, "Synthetic: written for the wrong visit.", CancellationToken.None)).Status != 200)
                    {
                        throw new InvalidOperationException("The benchmark's visit summary was not withdrawn.");
                    }

                    summary = await ReleaseAsync();
                }

                standing.Add((patient, note.Id, summary));
            }
        }

        await transaction.CommitAsync();
        return standing;
    }

    static async Task AcknowledgeResultsAsync(BenchApp app, Guid clinicianId, List<(Guid Patient, Guid Order)> resulted)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<UserContext>().UserId = clinicianId;
        var db = scope.ServiceProvider.GetRequiredService<EhrDbContext>();
        var lab = scope.ServiceProvider.GetRequiredService<Ehr.Domain.Results.LabResults>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        foreach (var (patient, order) in resulted)
        {
            if (!await lab.AcknowledgeAsync(patient, order, CancellationToken.None))
            {
                throw new InvalidOperationException("The benchmark's acknowledgment was refused.");
            }
        }

        await transaction.CommitAsync();
    }

    // The clinician records a proxy for a patient on their panel, through the app's own service and the database's rules.
    static async Task<Guid> RecordProxyAsync(BenchApp app, Guid clinicianId, Guid patient, string proxyEmail)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<UserContext>().UserId = clinicianId;
        var db = scope.ServiceProvider.GetRequiredService<EhrDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var access = scope.ServiceProvider.GetRequiredService<Ehr.Domain.Proxies.ProxyAccess>();
        if ((await access.GiveAsync(patient, [], proxyEmail, Ehr.Data.Proxies.ProxyScope.RecordAndMessages, includeSensitive: false, 30, CancellationToken.None)).Result
            != Ehr.Domain.Proxies.GiveResult.Given)
        {
            throw new InvalidOperationException("The benchmark's proxy grant was refused.");
        }

        var grant = await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM ehr.proxy_grants WHERE patient_id = {patient} AND revoked_at IS NULL").SingleAsync();
        await transaction.CommitAsync();
        return grant;
    }

    // Refill requests (step 13d). For each record, the clinician adds two coded medications to its chart, so there is
    // something to ask for and one left waiting; then, as the portal account (on its own record, or acting under the
    // proxy grant), a request for every medication the refill page offers, through the app's own service and the
    // database's rules; then the clinician declines the first of each, so the portal lists an answer too. With --stack
    // they stay on the primary afterwards, as its orders do. Every request, by patient, and whether it still waits.
    static async Task<List<(Guid Patient, Guid Request, bool Waiting)>> AskRefillsAsync(BenchApp app, Guid clinicianId, Guid account, (Guid Patient, Guid? Grant)[] records)
    {
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<UserContext>().UserId = clinicianId;
            var db = scope.ServiceProvider.GetRequiredService<EhrDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            foreach (var (patient, _) in records)
            {
                foreach (var (code, display) in new[] { ("314076", "lisinopril 10 MG Oral Tablet"), ("197361", "amlodipine 5 MG Oral Tablet") })
                {
                    db.Add(new Ehr.Data.Patients.MedicationStatement
                    {
                        Id = Guid.CreateVersion7(),
                        PatientId = patient,
                        Code = code,
                        Display = display,
                        Dosage = "1 tablet by mouth daily",
                        Status = Ehr.Data.Patients.MedicationStatementStatus.Active,
                    });
                }
            }

            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        foreach (var (patient, grant) in records)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var user = scope.ServiceProvider.GetRequiredService<UserContext>();
            (user.UserId, user.ActingGrant) = (account, grant);
            var db = scope.ServiceProvider.GetRequiredService<EhrDbContext>();
            var refills = scope.ServiceProvider.GetRequiredService<Ehr.Domain.Refills.RefillRequests>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var page = await scope.ServiceProvider.GetRequiredService<Ehr.Domain.Phi.PhiReader>().RefillsAsync(patient, sensitive: false, 50, CancellationToken.None)
                ?? throw new InvalidOperationException("The benchmark's portal account can't read the record it asks for.");
            foreach (var medication in page.Medications)
            {
                if (await refills.AskAsync(patient, medication.Id, "Synthetic: running low.", CancellationToken.None) != Ehr.Domain.Refills.RefillAsked.Sent)
                {
                    throw new InvalidOperationException("The benchmark's refill request was refused.");
                }
            }

            await transaction.CommitAsync();
        }

        var requests = new List<(Guid, Guid, bool)>();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<UserContext>().UserId = clinicianId;
            var db = scope.ServiceProvider.GetRequiredService<EhrDbContext>();
            var refills = scope.ServiceProvider.GetRequiredService<Ehr.Domain.Refills.RefillRequests>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            foreach (var (patient, _) in records)
            {
                var asked = await db.RefillRequests.AsNoTracking().Where(r => r.PatientId == patient).OrderBy(r => r.RequestedAt).Select(r => r.Id).ToListAsync();
                if (asked.Count < 2
                    || await refills.DeclineAsync(patient, asked[0], "Synthetic: please book a visit first.", CancellationToken.None) != Ehr.Domain.Refills.RefillDeclined.Declined)
                {
                    throw new InvalidOperationException("The benchmark's refill requests weren't asked for, or the first wasn't declined.");
                }

                requests.AddRange(asked.Select((request, i) => (patient, request, i > 0)));
            }

            await transaction.CommitAsync();
        }

        return requests;
    }

    // A request's page, for any of the requests, and the order form that approves one, for the waiting ones.
    static async Task<List<Result>> MeasureRefillsAsync(HttpClient client, DatabaseTimes times, string site, List<(Guid Patient, Guid Request, bool Waiting)> refills)
    {
        var random = new Random(20261126);
        var waiting = refills.Where(r => r.Waiting).ToList();
        return
        [
            await MeasureAsync(client, times, $"{site}Refill request, a patient on the panel", typing: false, () =>
            {
                var (patient, request, _) = refills[random.Next(refills.Count)];
                return $"/patients/{patient}/refills/{request}";
            }),
            await MeasureAsync(client, times, $"{site}Order form approving a refill, a patient on the panel", typing: false, () =>
            {
                var (patient, request, _) = waiting[random.Next(waiting.Count)];
                return $"/patients/{patient}/orders/new?type=medication&refill={request}";
            }),
        ];
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

    // Step 13e: what the forms' rows open. The patient, one answered form (a PHQ-9 with an answer to review) and one
    // still open (a GAD-7), which the portal's rows open.
    sealed record FormRows(Guid Patient, Guid Response, Guid Open);

    // For the patient, three appointments in the days ahead, each with a PHQ-9 and a GAD-7 asked for by the clinician,
    // then all but the last GAD-7 answered by the patient's own portal account: through the app's own service and the
    // database's rules, which score them. One PHQ-9 has an answer to review, which puts an item in the clinician's inbox.
    static async Task<FormRows> PreVisitFormsAsync(BenchApp app, Guid clinicianId, Guid patientUserId, Guid patient)
    {
        var requests = new List<(Guid Id, string Code, DateTimeOffset StartsAt)>();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<UserContext>().UserId = clinicianId;
            var db = scope.ServiceProvider.GetRequiredService<EhrDbContext>();
            var forms = scope.ServiceProvider.GetRequiredService<Ehr.Domain.Forms.PreVisitForms>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            for (var day = 1; day <= 3; day++)
            {
                var appointment = new Ehr.Data.Scheduling.Appointment
                {
                    Id = Guid.CreateVersion7(),
                    PatientId = patient,
                    ClinicianId = clinicianId,
                    StartsAt = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(day).AddHours(16), TimeSpan.Zero),
                    Minutes = 20,
                    Reason = "Synthetic: follow-up",
                    Status = Ehr.Data.Scheduling.AppointmentStatus.Booked,
                };
                db.Add(appointment);
                await db.SaveChangesAsync();
                foreach (var code in new[] { "44249-1", "69737-5" })
                {
                    if (await forms.AskAsync(patient, appointment.Id, code, CancellationToken.None) != Ehr.Domain.Forms.FormResult.Saved)
                    {
                        throw new InvalidOperationException("The benchmark's pre-visit form was refused.");
                    }
                }
            }

            requests.AddRange((await db.QuestionnaireRequests.AsNoTracking().Where(r => r.PatientId == patient)
                .Join(db.Appointments, r => r.AppointmentId, a => a.Id, (r, a) => new { r.Id, r.QuestionnaireCode, a.StartsAt }).ToListAsync())
                .Select(r => (r.Id, r.QuestionnaireCode, r.StartsAt)).OrderBy(r => r.StartsAt).ThenBy(r => r.QuestionnaireCode));
            await transaction.CommitAsync();
        }

        // Each answer by its total: the PHQ-9's 6, 12 (question 9 "Several days", so to review) and 8; the GAD-7's 4 and 11.
        string[] phq = ["44250-9", "44255-8", "44259-0", "44254-1", "44251-7", "44258-2", "44252-5", "44253-3", "44260-8"];
        string[] gad = ["69725-0", "68509-9", "69733-4", "69734-2", "69735-9", "69689-8", "69736-7"];
        string[] answers = ["LA6568-5", "LA6569-3", "LA6570-1", "LA6571-9"];
        Dictionary<string, string> Answers(string[] items, int[] scores) => items.Select((item, place) => (item, answers[scores[place]])).ToDictionary(pair => pair.item, pair => pair.Item2);
        var sent = new Dictionary<int, Dictionary<string, string>>
        {
            [0] = Answers(phq, [1, 1, 1, 1, 1, 1, 0, 0, 0]),
            [1] = Answers(gad, [1, 1, 1, 1, 0, 0, 0]),
            [2] = Answers(phq, [2, 2, 2, 2, 1, 1, 1, 0, 1]),
            [3] = Answers(gad, [2, 2, 2, 2, 1, 1, 1]),
            [4] = Answers(phq, [1, 1, 1, 1, 1, 1, 1, 1, 0]),
        };
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<UserContext>().UserId = patientUserId;
            var db = scope.ServiceProvider.GetRequiredService<EhrDbContext>();
            var forms = scope.ServiceProvider.GetRequiredService<Ehr.Domain.Forms.PreVisitForms>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            foreach (var (place, given) in sent)
            {
                if (await forms.SendAsync(patient, requests[place].Id, given, CancellationToken.None) != Ehr.Domain.Forms.FormResult.Saved)
                {
                    throw new InvalidOperationException("The benchmark's pre-visit form's answers were refused.");
                }
            }

            await transaction.CommitAsync();
        }

        await using var read = app.Services.CreateAsyncScope();
        read.ServiceProvider.GetRequiredService<UserContext>().UserId = clinicianId;
        var responses = read.ServiceProvider.GetRequiredService<EhrDbContext>();
        await using var reading = await responses.Database.BeginTransactionAsync();
        var review = await responses.QuestionnaireResponses.AsNoTracking().Where(r => r.RequestId == requests[2].Id).Select(r => r.Id).SingleAsync();
        await reading.CommitAsync();
        return new FormRows(patient, review, requests[5].Id);
    }

    // The clinician's pre-visit forms pages: the patient's forms hidden, then shown, and one form's answers.
    static async Task<List<Result>> MeasureFormsAsync(HttpClient client, DatabaseTimes times, string prefix, FormRows forms) =>
    [
        await MeasureAsync(client, times, prefix + "Pre-visit forms, a patient on the panel", typing: false, () => $"/patients/{forms.Patient}/forms"),
        await MeasureAsync(client, times, prefix + "Pre-visit forms with sensitive items, a patient on the panel", typing: false,
            () => $"/patients/{forms.Patient}/forms?sensitive=true"),
        await MeasureAsync(client, times, prefix + "Pre-visit form answers, a patient on the panel", typing: false,
            () => $"/patients/{forms.Patient}/forms/{forms.Response}"),
    ];

    // The portal's pre-visit forms pages, as the patient: the list, and the form still open.
    static async Task<List<Result>> MeasurePortalFormsAsync(HttpClient portal, DatabaseTimes times, string prefix, FormRows forms) =>
    [
        await MeasureAsync(portal, times, prefix + "Portal pre-visit forms, the patient's own", typing: false, () => "/portal/forms"),
        await MeasureAsync(portal, times, prefix + "Portal pre-visit form, the patient's own", typing: false, () => $"/portal/forms/{forms.Open}"),
    ];

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

    const int ViewsPerRecord = 20_000;

    const int EndedAssignments = 5_000;

    // Step 13c2: the benchmark's clinician's ended assignments.
    const int ClinicianAssignments = 60_000;

    // Step 13c: the panels' table as a clinic's would be after a year or two, which a page of a proxy without sensitive
    // items reads for the patient's history (42-record-views.sql): 5,000 ended assignments of seeded patients, with
    // twenty colleagues of the benchmark's clinician (disabled accounts, so nobody signs in as them), three of them for
    // each record given, by colleagues, ended two years ago: they change no line the pages show. Without these the table
    // held the run's hundred, and a page that read it badly looked fast. And step 13c2's: the benchmark's clinician with
    // 60,000 ended assignments of seeded patients, a long career's, and an emergency-access window on each record given
    // for four hours of every day of the year of views (FillViewsAsync), so a page asks, for each of the clinician's views
    // in a window, whether the record was on their panel then, which read all 60,000 before the history's index. As the
    // database's superuser, in the benchmark's own database only: with --stack the tables are the stack's.
    static async Task FillPanelHistoryAsync(MigratedDatabase? database, Guid clinicianId, params Guid[] records)
    {
        if (database is null)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            WITH colleagues AS (
                INSERT INTO ehr.staff_users (id, user_name, normalized_user_name, email, normalized_email, email_confirmed, phone_number_confirmed,
                    two_factor_enabled, lockout_enabled, lockout_end, access_failed_count, display_name, roles, must_change_password)
                SELECT gen_random_uuid(), x.email, upper(x.email), x.email, upper(x.email), true, false, false, true, now() + interval '100 years', 0,
                       'Synthetic Colleague ' || n, ARRAY['Clinician'], false
                FROM generate_series(1, 20) AS n, LATERAL (SELECT 'bench-colleague-' || n || '-' || gen_random_uuid() || '@example.test' AS email) x
                RETURNING id
            ),
            numbered AS (SELECT id, row_number() OVER (ORDER BY id) - 1 AS k FROM colleagues),
            seeded AS (SELECT p.id, row_number() OVER (ORDER BY p.id) AS n FROM ehr.patients p LIMIT @count)
            INSERT INTO ehr.patient_assignments (id, patient_id, clinician_id, assigned_at, ended_at)
            SELECT gen_random_uuid(), p.id, c.id, now() - make_interval(days => 700 - (p.n % 300)::int), now() - make_interval(days => 400 - (p.n % 300)::int)
            FROM seeded p JOIN numbered c ON c.k = p.n % 20
            UNION ALL
            SELECT gen_random_uuid(), r.patient, c.id, now() - interval '3 years', now() - interval '2 years'
            FROM unnest(@records) AS r(patient) CROSS JOIN (SELECT id FROM numbered WHERE k < 3) c
            UNION ALL
            SELECT gen_random_uuid(), p.id, @clinician, now() - make_interval(days => 800 + t.round), now() - make_interval(days => 799 + t.round)
            FROM seeded p CROSS JOIN generate_series(1, @careers) AS t(round);
            INSERT INTO ehr.break_glass_accesses (id, patient_id, clinician_id, reason, granted_at, expires_at)
            SELECT gen_random_uuid(), r.patient, @clinician, 'Synthetic reason for the benchmark.', now() - make_interval(days => d, hours => 4), now() - make_interval(days => d)
            FROM unnest(@records) AS r(patient) CROSS JOIN generate_series(0, 365) AS d;
            ANALYZE ehr.patient_assignments;
            ANALYZE ehr.break_glass_accesses;
            """, connection);
        command.Parameters.AddWithValue("count", EndedAssignments);
        command.Parameters.AddWithValue("records", records);
        command.Parameters.AddWithValue("clinician", clinicianId);
        command.Parameters.AddWithValue("careers", ClinicianAssignments / EndedAssignments);
        await command.ExecuteNonQueryAsync();
    }

    // Step 13c: a year of views of each record in the audit log, 26 minutes apart, beyond those the run's own pages make.
    // One in twenty is the clinician's read of the record; the rest are views of sensitive items, of a note, of a
    // summary marked sensitive, or by another portal account, and events that are no view at all (a sign-in, writes).
    // Half the views have the part they were for (step 13c2): the chart, with the notes it lists, a note's page, the
    // visit summaries, the portal's home; half have none, as before that step.
    // The first record's patient is shown all the views. The proxy of the second, who has no sensitive items, is shown
    // none of them, not even the clinician's, made before the run put the record on the clinician's panel: so a page of
    // theirs reads past the whole year, the most a page can read. As the database's superuser,
    // in the benchmark's own database only: with --stack the pages read the run's own views.
    static async Task FillViewsAsync(MigratedDatabase? database, Guid clinicianId, params Guid[] records)
    {
        if (database is null)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source, sensitive, part)
            SELECT uuidv7(), now() - make_interval(mins => n * 26), v.actor, v.action, v.table_name, r.patient, r.patient, 'onprem', v.sensitive,
                   CASE WHEN v.action NOT IN ('read', 'read_sensitive') OR n % 40 >= 20 THEN NULL
                        WHEN v.table_name = 'clinical_notes' THEN CASE WHEN n % 20 = 19 THEN 'visit-note' ELSE 'chart' END
                        WHEN v.table_name = 'visit_summaries' THEN 'summaries'
                        WHEN v.actor = @other THEN 'home'
                        ELSE 'chart' END
            FROM unnest(@records) AS r(patient)
            CROSS JOIN generate_series(1, @count) AS n
            CROSS JOIN LATERAL (SELECT
                CASE WHEN n % 20 BETWEEN 11 AND 16 THEN @other ELSE @clinician END AS actor,
                CASE n % 20 WHEN 1 THEN 'read_sensitive' WHEN 16 THEN 'sign_in' WHEN 17 THEN 'insert' WHEN 18 THEN 'update' ELSE 'read' END AS action,
                CASE WHEN n % 20 BETWEEN 2 AND 9 OR n % 20 = 19 THEN 'clinical_notes' WHEN n % 20 = 10 THEN 'visit_summaries'
                     WHEN n % 20 = 16 THEN NULL WHEN n % 20 = 17 THEN 'conditions' ELSE 'patients' END AS table_name,
                n % 20 = 10 AS sensitive) AS v
            """, connection);
        command.Parameters.AddWithValue("clinician", clinicianId);
        command.Parameters.AddWithValue("other", Guid.CreateVersion7());
        command.Parameters.AddWithValue("records", records);
        command.Parameters.AddWithValue("count", ViewsPerRecord);
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
                // Required outside Development (step 6c). The benchmark sends no email and issues no enrollment code.
                builder.UseSetting("Ehr:Links:ClinicalBaseUrl", "https://localhost").UseSetting("Ehr:Links:PortalBaseUrl", "https://localhost")
                    .UseSetting("Ehr:Clinic:TimeZone", "America/Chicago").UseSetting("Ehr:Clinic:TimeZoneAbbreviations", "CST,CDT")
                    .UseSetting("Ehr:Enrollment:Key", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))
                    // A lab is connected, as in the tests' databases (step 12), and may ask as fast as the rows do.
                    .UseSetting("Ehr:Lab:Key", MigratedDatabase.LabKey)
                    .UseSetting("RateLimits:LabRequestsPerMinute", "1000000");
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
                .UseSetting("Ehr:Links:ClinicalBaseUrl", "https://localhost").UseSetting("Ehr:Links:PortalBaseUrl", "https://localhost")
                .UseSetting("Ehr:Clinic:TimeZone", "America/Chicago").UseSetting("Ehr:Clinic:TimeZoneAbbreviations", "CST,CDT")
                .UseSetting("Ehr:Enrollment:Key", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))
                .UseSetting("Logging:LogLevel:Default", "Warning")
                .UseSetting("Logging:LogLevel:Microsoft.AspNetCore.DataProtection", "Critical")
                .UseSetting("Logging:LogLevel:Ehr.Web.CloudAudit.CloudAuditShipper", "Error")
                .ConfigureTestServices(services =>
                {
                    services.AddSingleton(keys);
                    services.AddSingleton(new OnPremApp(OnPremUrl, new HttpMessageInvoker(new LinkDown())));
                    services.AddSingleton<IDurableAuditQueue>(provider => new TimedAuditQueue(provider.GetRequiredService<CloudAuditQueue>(), times));
                    services.ConfigureDbContext<EhrDbContext>(options => options.AddInterceptors(times));
                });

        sealed class LinkDown : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new HttpRequestException("The link to the on-prem app is down.", new SocketException((int)SocketError.ConnectionRefused));
        }
    }
}
