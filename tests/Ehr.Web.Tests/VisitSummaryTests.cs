using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Ehr.Data;
using Ehr.Data.Audit;
using Ehr.Domain.Audit;
using Ehr.Domain.Phi;
using Ehr.Testing;
using Ehr.Web.Areas.Portal.Pages;
using Ehr.Web.CloudAudit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;

namespace Ehr.Web.Tests;

// Visit summaries (the expansion plan's step 13a): helpers for the test cases TC-UC14-18 to 25 and TC-UC6-15 to 23,
// and the chart's one supporting test. What the database refuses whatever the app does is in Ehr.Security.Tests
// (VisitSummarySecurityTests).
public class VisitSummaryTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // The three parts of the summary the tests release, unless they say otherwise.
    public const string TalkedAbout = "We talked about your cough and how you have been sleeping.";
    public const string Found = "Your chest sounded clear.\nYour temperature was normal.";
    public const string NextSteps = "Rest, drink plenty of fluids, and call us if the cough lasts more than two weeks.";

    // A note's sections no summary or portal page may ever show: the note's own words.
    public const string NoteSubjective = "Synthetic subjective: dry cough, quartzwing pattern, nocturnal.";
    public const string NoteAssessment = "Synthetic assessment: viral illness.";
    public const string NotePlan = "Synthetic plan: rest and follow-up.";

    public static string SummaryPath(Guid patient, Guid note) => NotePath(patient, note) + "/summary";

    public static string WithdrawPath(Guid patient, Guid note, Guid summary) => NotePath(patient, note) + $"/summaries/{summary}/withdraw";

    // "Release to the patient", as the page posts it, "Sensitive" ticked or not.
    public static Task<HttpResponseMessage> ReleaseAsync(
        HttpClient client, Guid patient, Guid note, string talkedAbout = TalkedAbout, string found = Found, string nextSteps = NextSteps, bool sensitive = false) =>
        PostFormAsync(client, SummaryPath(patient, note), SummaryPath(patient, note), ("TalkedAbout", talkedAbout), ("Found", found), ("NextSteps", nextSteps), ("Sensitive", sensitive));

    // "Mark sensitive" (mark: true) or "Unmark as sensitive" (false) for a summary, as its sensitivity page posts it.
    public static Task<HttpResponseMessage> ChangeSummarySensitivityAsync(HttpClient client, Guid patient, Guid note, Guid summary, bool mark, string reason)
    {
        var path = SensitivityPath(patient, note) + $"?summary={summary}";
        return PostFormAsync(client, path, path, ("Mark", mark), ("Reason", reason));
    }

    // "Withdraw summary", as the page posts it.
    public static Task<HttpResponseMessage> WithdrawAsync(HttpClient client, Guid patient, Guid note, Guid summary, string reason = "Written for the wrong visit.") =>
        PostFormAsync(client, WithdrawPath(patient, note, summary), WithdrawPath(patient, note, summary), ("Reason", reason));

    // "released-by sensitive standing|withdrawn what-we-talked-about" for the patient's summaries, in the order released.
    public static Task<List<string>> SummariesAsync(WebApps apps, Guid patient) => ProxyTests.QueryAsync(apps,
        """
        SELECT released_by || ' ' || sensitive || ' ' || CASE WHEN withdrawn_at IS NULL THEN 'standing' ELSE 'withdrawn' END || ' ' || talked_about
        FROM ehr.visit_summaries WHERE patient_id = @patient ORDER BY released_at, id
        """, patient);

    // The patient's standing summary of a note, when there is one: its ID.
    public static async Task<Guid> StandingAsync(WebApps apps, Guid patient) =>
        Guid.Parse((await ProxyTests.QueryAsync(apps, "SELECT id::text FROM ehr.visit_summaries WHERE patient_id = @patient AND withdrawn_at IS NULL ORDER BY released_at DESC LIMIT 1", patient)).Single());

    // A clinician with a patient on their panel, and the patient signed in to the portal (step 13a's portal tests).
    internal static async Task<(StaffAccount Clinician, Session Session, ProxyTests.Person Patient)> PortalPatientAsync(WebApps apps, string family = "Summaries")
    {
        var clinician = await apps.ClinicianAsync();
        var person = await ProxyTests.PersonAsync(apps, "Synthetic", family);
        await apps.AssignAsync(clinician, person.Patient);
        return (clinician, await SignInAsync(apps.OnPrem, clinician), person);
    }

    // Someone the patient gives access to their record, signed in and looking at it: their session and the grant.
    internal static async Task<(ProxyTests.Person Proxy, Guid Grant)> ProxyForAsync(WebApps apps, ProxyTests.Person patient, string family, bool sensitive, string scope = "record")
    {
        var proxy = await ProxyTests.PersonAsync(apps, "Synthetic", family);
        await ProxyTests.GiveAsync(patient, proxy.Email, scope, sensitive);
        var grant = Guid.Parse((await ProxyTests.QueryAsync(apps,
            $"SELECT id::text FROM ehr.proxy_grants WHERE patient_id = @patient AND lower(proxy_email) = lower('{proxy.Email}') AND revoked_at IS NULL", patient.Patient)).Single());
        await ProxyTests.ViewAsync(proxy, grant);
        return (proxy, grant);
    }

    // The portal's visit summaries, as its client reads them, sensitive ones asked for or not.
    public static Task<string> PortalAsync(HttpClient client, bool sensitive = false) =>
        client.GetStringAsync("/portal/summaries" + (sensitive ? "?sensitive=true" : ""), Cancel);

    // A page's markup without its form tokens, which differ on every response.
    public static string WithoutTokens(string html) => Regex.Replace(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"[^\"]*\"", "");

    // A note's date as the portal shows it: as the results page dates a result.
    public static async Task<string> PortalDateAsync(WebApps apps, Guid patient, Guid note) =>
        DateTimeOffset.Parse((await ProxyTests.QueryAsync(apps, $"SELECT to_char(signed_at AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS\"Z\"') FROM ehr.clinical_notes WHERE patient_id = @patient AND id = '{note}'", patient)).Single(),
            CultureInfo.InvariantCulture).UtcDateTime.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) + " UTC";

    // The emails sent to an address, oldest first.
    public static List<(string To, string Subject, string Body)> EmailsTo(WebApps apps, string address) =>
        [.. apps.Emails.Sent.Where(message => string.Equals(message.To, address, StringComparison.OrdinalIgnoreCase))];

    // The rule about codes looks each word of a code's shape up by index, once (db/policies/52-visit-summaries.sql), so
    // ordinary words like "B12" cost next to nothing. Before it did, each such word cost a read of the code tables, under
    // the note's lock: on an idle laptop a release with twenty of them took 0.6 s, against 9 ms for a plain one, so fifty
    // would take about 1.5 s. Now fifty take about as long as none. The bound, a second for the fastest of three
    // releases, leaves a busy machine a wide margin over that and is still under what the old rule took on an idle one;
    // the fastest, because a busy machine can stall any one request, and the rule's cost is in every one.
    [Fact]
    public async Task A_summary_with_fifty_ordinary_words_shaped_like_codes_is_released_in_well_under_a_second()
    {
        var (_, patient, session) = await OrderTests.OnPanelAsync(apps);
        // Dosing times, vitamins, a gene, spinal levels and the like, and made-up ones of the same shape: none is a code.
        string[] words = ["A1c", "B12", "H2O", "Q2h", "Q3h", "Q4h", "Q6h", "Q8h", "Q12h", "Q24h", "G6PD", "H1N1", "L5S1", "C5a", "P2Y12",
            .. Enumerable.Range(0, 35).Select(i => $"{(char)('A' + i % 26)}{i % 9 + 1}{(char)('t' + i / 26)}")];
        Assert.Equal(50, words.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var found = "Words that look like codes and aren't: " + string.Join(", ", words) + ".";
        using var client = Client(apps.OnPrem, session.Jar, followRedirects: false);
        var fastest = TimeSpan.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var note = await SignedNoteAsync(session.Client, patient);
            using var page = await session.Client.GetAsync(SummaryPath(patient, note), Cancel);
            var watch = Stopwatch.StartNew();
            using var released = await PostFormAsync(client, SummaryPath(patient, note), page,
                ("TalkedAbout", TalkedAbout), ("Found", found), ("NextSteps", NextSteps), ("Sensitive", false));
            watch.Stop();
            Assert.Equal(HttpStatusCode.Redirect, released.StatusCode);
            fastest = watch.Elapsed < fastest ? watch.Elapsed : fastest;
        }

        Assert.True(fastest < TimeSpan.FromSeconds(1), $"Released in {fastest.TotalMilliseconds:F0} ms at the fastest.");
    }

    // A refusal marks only the parts it names as invalid, and ties each of its messages to that part, the hint after them
    // (aria-describedby); the other parts keep the hint alone.
    [Fact]
    public async Task A_refusal_marks_only_the_parts_it_names_each_tied_to_its_messages()
    {
        var (_, patient, session) = await OrderTests.OnPanelAsync(apps);
        var note = await SignedNoteAsync(session.Client, patient);

        using (var coded = await ReleaseAsync(session.Client, patient, note, found: "We checked E11.9 and 4548-4."))
        {
            var page = await coded.Content.ReadAsStringAsync(Cancel);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, coded.StatusCode);
            Assert.Equal(
                [("summary-talked-about", "summary-hint", ""), ("summary-found", "summary-error-0 summary-error-1 summary-hint", "true"), ("summary-next-steps", "summary-hint", "")],
                Fields(page));
            Assert.StartsWith("Not released: “E11.9” in What we found", Message(page, 0));
            Assert.StartsWith("Not released: “4548-4” in What we found", Message(page, 1));
        }

        using (var empty = await ReleaseAsync(session.Client, patient, note, talkedAbout: " ", nextSteps: ""))
        {
            var page = await empty.Content.ReadAsStringAsync(Cancel);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
            Assert.Equal(
                [("summary-talked-about", "summary-error-0 summary-hint", "true"), ("summary-found", "summary-hint", ""), ("summary-next-steps", "summary-error-1 summary-hint", "true")],
                Fields(page));
            Assert.Equal("Not released: Write “What we talked about”.", Message(page, 0));
            Assert.Equal("Not released: Write “What happens next”.", Message(page, 1));
        }

        // Each part's ID, what describes it and whether it is invalid, in the page's order.
        static List<(string, string, string)> Fields(string page) =>
            [.. Regex.Matches(page, "<textarea id=\"([^\"]+)\"[^>]*aria-describedby=\"([^\"]*)\"(?: aria-invalid=\"([^\"]*)\")?")
                .Select(field => (field.Groups[1].Value, field.Groups[2].Value, field.Groups[3].Value))];

        static string Message(string page, int index) =>
            Words(Regex.Match(page, $"<p id=\"summary-error-{index}\">(.*?)</p>").Groups[1].Value);
    }

    // At the page's cap, fifty summaries, the portal's page records 51 reads a view: the patient's and one for each
    // summary shown. On-site they are written in one statement, and off-site given to the cloud app's queue in one
    // write; the page still shows all fifty. Every statement the read sends is counted as Npgsql traces it, EF Core's
    // and the raw ones alike.
    [Fact]
    public async Task At_the_cap_the_portal_records_its_reads_in_one_statement_on_site_and_one_queue_write_off_site()
    {
        var (clinician, _, person) = await PortalPatientAsync(apps, "Capped");
        await FiftyAsync(apps, clinician.Id, person.Patient);

        // The patient's reads recorded so far, by table.
        Task<List<string>> ReadsAsync() => ProxyTests.QueryAsync(apps,
            "SELECT table_name || ' ' || count(*) FROM ehr.audit_events WHERE patient_id = @patient AND action = 'read' AND actor_id IS NOT NULL GROUP BY table_name ORDER BY table_name",
            person.Patient);
        var before = await ReadsAsync();

        // On-site, through the reader, as the patient's own portal account.
        using var read = new Activity("visit-summaries-at-the-cap").Start();
        var sent = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.RootId == read.RootId)
                {
                    lock (sent)
                    {
                        sent.Add(activity.GetTagItem("db.query.text") as string ?? "");
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        var user = new UserContext { UserId = person.Account };
        var options = new DbContextOptionsBuilder<EhrDbContext>();
        EhrDbContext.Configure(options, apps.Database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword));
        options.AddInterceptors(new UserContextInterceptor(user));
        await using (var db = new EhrDbContext(options.Options))
        {
            var reader = new PhiReader(db, new AuditRecorder(db, user, new AuditSource(AuditSources.OnPrem)), user);
            await using var transaction = await db.Database.BeginTransactionAsync(Cancel);
            Assert.Equal(SummariesModel.Limit, (await reader.VisitSummariesAsync(person.Patient, sensitive: false, SummariesModel.Limit, AuditParts.Summaries, Cancel))!.Summaries.Count);
            await transaction.CommitAsync(Cancel);
        }

        read.Stop();
        var written = Assert.Single(sent, text => text.Contains("ehr.audit_events", StringComparison.Ordinal));
        Assert.Equal(1, Regex.Count(written, "INSERT INTO"));
        Assert.Equal([$"patients {Count(before, "patients") + 1}", $"visit_summaries {SummariesModel.Limit}"], await ReadsAsync());

        // The page itself, on-site: all fifty.
        Assert.Equal(SummariesModel.Limit, Regex.Count(await PortalAsync(person.Client), "data-summary=\"standing\""));

        // Off-site: one write to the queue, of all 51, and all fifty shown.
        var queue = new CountedQueue(apps.CloudCutOff.Services.GetRequiredService<CloudAuditQueue>());
        await using var cloud = apps.CloudCutOff.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IDurableAuditQueue>(queue)));
        Assert.Equal(SummariesModel.Limit, Regex.Count(await PortalAsync(Client(cloud, person.Jar)), "data-summary=\"standing\""));
        Assert.Equal([SummariesModel.Limit + 1], queue.Writes);
    }

    static int Count(List<string> reads, string table) =>
        reads.Where(read => read.StartsWith(table + " ", StringComparison.Ordinal)).Select(read => int.Parse(read[(table.Length + 1)..], CultureInfo.InvariantCulture)).SingleOrDefault();

    // The cloud app's queue, counting each write and how many events it held.
    sealed class CountedQueue(IDurableAuditQueue queue) : IDurableAuditQueue
    {
        public List<int> Writes { get; } = [];

        public void Enqueue(IReadOnlyCollection<AuditEvent> auditEvents)
        {
            queue.Enqueue(auditEvents);
            Writes.Add(auditEvents.Count);
        }
    }

    // Fifty notes signed and a summary released for each, as the clinician, in one transaction.
    static async Task FiftyAsync(WebApps apps, Guid clinician, Guid patient)
    {
        await using var connection = new NpgsqlConnection(apps.Database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword));
        await connection.OpenAsync(Cancel);
        await using var transaction = await connection.BeginTransactionAsync(Cancel);
        await using var command = new NpgsqlCommand(
            """
            SELECT set_config('app.user_id', @clinician::text, true);
            INSERT INTO ehr.clinical_drafts
                (id, patient_id, clinician_id, body, version, subjective, objective, assessment, plan, structured_version, has_unreviewed_proposals)
            SELECT d, @patient, @clinician, 'Synthetic plain text', 1, 'Synthetic subjective.', 'Synthetic objective.', 'Synthetic assessment.', 'Synthetic plan.', 1, false
            FROM unnest(@drafts) AS d;
            INSERT INTO ehr.clinical_notes (id, draft_id, patient_id, draft_version, subjective, objective, assessment, plan)
            SELECT n.id, n.draft, @patient, 1, 'Synthetic subjective.', 'Synthetic objective.', 'Synthetic assessment.', 'Synthetic plan.'
            FROM unnest(@notes, @drafts) AS n(id, draft);
            INSERT INTO ehr.visit_summaries (id, note_id, patient_id, talked_about, found, next_steps, sensitive)
            SELECT uuidv7(), n, @patient, @talked, @found, @next, false FROM unnest(@notes) AS n;
            """, connection, transaction);
        command.Parameters.AddWithValue("clinician", clinician);
        command.Parameters.AddWithValue("patient", patient);
        command.Parameters.AddWithValue("drafts", Enumerable.Range(0, SummariesModel.Limit).Select(_ => Guid.CreateVersion7()).ToArray());
        command.Parameters.AddWithValue("notes", Enumerable.Range(0, SummariesModel.Limit).Select(_ => Guid.CreateVersion7()).ToArray());
        command.Parameters.AddWithValue("talked", TalkedAbout);
        command.Parameters.AddWithValue("found", Found);
        command.Parameters.AddWithValue("next", NextSteps);
        await command.ExecuteNonQueryAsync(Cancel);
        await transaction.CommitAsync(Cancel);
    }

    // The chart shows which signed notes have a summary in the portal from the notes statement it already makes: the
    // chart is the page nearest the render gate, so the tag costs no statement of its own (step 7a).
    [Fact]
    public async Task The_charts_summary_tag_costs_no_statement_of_its_own()
    {
        var (clinician, patient, session) = await OrderTests.OnPanelAsync(apps);
        var note = await SignedNoteAsync(session.Client, patient);
        async Task<(List<string> Commands, PatientChart Chart)> ChartAsync()
        {
            var commands = new PatientSearchQueryTests.CapturedCommands();
            var user = new UserContext { UserId = clinician.Id };
            var options = new DbContextOptionsBuilder<EhrDbContext>();
            EhrDbContext.Configure(options, apps.Database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword));
            options.AddInterceptors(new UserContextInterceptor(user), commands);
            await using var db = new EhrDbContext(options.Options);
            var reader = new PhiReader(db, new AuditRecorder(db, user, new AuditSource(AuditSources.OnPrem)), user);
            await using var transaction = await db.Database.BeginTransactionAsync(Cancel);
            var chart = (await reader.ChartAsync(patient, 5, AuditParts.Chart, Cancel))!;
            await transaction.RollbackAsync(Cancel);
            return ([.. commands.Executed.Select(command => command.Text)], chart);
        }

        var (before, unsummarized) = await ChartAsync();
        Assert.False(Assert.Single(unsummarized.Notes).SummaryReleased);
        using var released = await ReleaseAsync(session.Client, patient, note);
        Assert.Contains("data-summary=\"standing\"", await ReadAsync(released));

        var (after, summarized) = await ChartAsync();

        Assert.True(Assert.Single(summarized.Notes).SummaryReleased);
        Assert.Equal(before.Count, after.Count);
        var notes = Assert.Single(after, text => text.Contains("visit_summaries", StringComparison.Ordinal));
        Assert.Contains("FROM ehr.clinical_notes", notes, StringComparison.Ordinal);
    }
}
