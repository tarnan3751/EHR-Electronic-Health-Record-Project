using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-15
// Purpose / scenario: The patient reads the visit summaries their care team wrote for them, from the portal's home:
//   newest first, each dated as the results page dates a result (the day the visit's notes were signed), with who wrote
//   it and its three parts as written, line breaks kept. Nothing of the notes themselves is on the page.
// Type: Functional (successful flow: visit summaries)
// Requirements: FR-16, proposed FR-55
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a confirmed portal account and is signed in on the on-site app. Dr. Test, with the
//   patient on their panel, has signed two notes and released a summary of each, the second note after the first.
// Inputs and system conditions: GET /portal, then GET /portal/summaries, on the on-site app.
// Test data: The first summary's What we talked about "We talked about your blood pressure."; the second's the test's
//   summary, What we found on two lines; the notes' Subjective "Synthetic subjective: dry cough, quartzwing pattern,
//   nocturnal." and Objective "Synthetic objective: chest clear.".
// Steps:
//   1. Open the portal's home and follow "See your visit summaries".
// Expected result: The page sends Cache-Control: no-store and lists the second summary before the first, each headed
//   with its note's date as "MMM d, yyyy UTC" and "Written by Dr. Test", with the headings "What we talked about",
//   "What we found" and "What happens next" and their text, line breaks kept. Neither note's text is on the page.
//   Opening it is one recorded read of the patient, and one recorded read of each summary shown, by the patient's own
//   account.
// Creator: Tahseen Arnan
public class TC_UC6_15_VisitSummaries(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_patient_reads_their_visit_summaries_newest_first_and_nothing_of_the_notes()
    {
        var (_, session, patient) = await PortalPatientAsync(apps);
        var first = await SignedNoteAsync(session.Client, patient.Patient, subjective: NoteSubjective);
        using (var released = await ReleaseAsync(session.Client, patient.Patient, first, "We talked about your blood pressure."))
        {
            Assert.Contains("Released:", Words(await ReadAsync(released)));
        }

        var second = await SignedNoteAsync(session.Client, patient.Patient, subjective: NoteSubjective);
        using (var released = await ReleaseAsync(session.Client, patient.Patient, second))
        {
            Assert.Contains("Released:", Words(await ReadAsync(released)));
        }

        var home = await patient.Client.GetStringAsync("/portal", Cancel);
        Assert.Contains("href=\"/portal/summaries\"", home);
        Assert.Contains("See your visit summaries", home);
        var readsBefore = await apps.AuditEventsAsync(AuditActions.Read, patient.Account, patient.Patient);

        using var response = await patient.Client.GetAsync("/portal/summaries", Cancel);
        var html = await ReadAsync(response);
        var words = Words(html);

        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(words.IndexOf(TalkedAbout, StringComparison.Ordinal) < words.IndexOf("We talked about your blood pressure.", StringComparison.Ordinal));
        Assert.Contains($"<h2>{await PortalDateAsync(apps, patient.Patient, second)}</h2>", html);
        Assert.Contains($"<h2>{await PortalDateAsync(apps, patient.Patient, first)}</h2>", html);
        Assert.Equal(2, CountOf(words, "Written by Dr. Test"));
        Assert.Equal(2, CountOf(html, "<h3>What we talked about</h3>"));
        Assert.Contains("<h3>What we found</h3>", html);
        Assert.Contains("<h3>What happens next</h3>", html);
        Assert.Contains("Your chest sounded clear.\nYour temperature was normal.", words);
        Assert.Contains(NextSteps, words);
        foreach (var noteText in new[] { NoteSubjective, Objective, NoteAssessment, NotePlan, "quartzwing" })
        {
            Assert.DoesNotContain(noteText, words);
        }

        // One read of the patient, and one of each summary shown, by the patient's own account.
        Assert.Equal(readsBefore + 3, await apps.AuditEventsAsync(AuditActions.Read, patient.Account, patient.Patient));
        Assert.Equal(["2"], await ProxyTests.QueryAsync(apps,
            $"SELECT count(*)::text FROM ehr.audit_events WHERE patient_id = @patient AND table_name = 'visit_summaries' AND action = 'read' AND actor_id = '{patient.Account}'", patient.Patient));
    }

    static int CountOf(string text, string part) => (text.Length - text.Replace(part, "", StringComparison.Ordinal).Length) / part.Length;
}
