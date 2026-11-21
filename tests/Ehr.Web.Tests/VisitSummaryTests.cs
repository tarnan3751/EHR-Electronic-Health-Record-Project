using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Ehr.Data;
using Ehr.Data.Audit;
using Ehr.Domain.Audit;
using Ehr.Domain.Phi;
using Ehr.Testing;
using Microsoft.EntityFrameworkCore;
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
    public const string NoteHistory = "Synthetic history: dry cough, quartzwing pattern, nocturnal.";
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
            var chart = (await reader.ChartAsync(patient, 5, Cancel))!;
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
