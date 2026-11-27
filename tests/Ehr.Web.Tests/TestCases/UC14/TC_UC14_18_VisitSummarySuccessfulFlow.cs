using Ehr.Data.Audit;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-18
// Purpose / scenario: From a signed note, a clinician writes a visit summary for the patient, in three parts typed
//   from nothing with the note under the form for reference, and releases it: the note's page shows it with who
//   released it and when, the chart tags the note, and the patient is told by email that there is something new in
//   their portal, with no health details and a link built from the portal's configured address. At most one such
//   email a day.
// Type: Functional (successful flow: visit summaries)
// Requirements: FR-31, proposed FR-55
// Use case: UC 14, Review and sign a clinical note
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel and has signed three
//   notes. The patient has a confirmed portal account.
// Inputs and system conditions: GET /patients/{id}/notes/{note} and .../summary; POST .../summary, the first with
//   the request's Host header set to another site; the clinic's clock is UTC in the tests.
// Test data: What we talked about "We talked about your cough and how you have been sleeping.", What we found "Your
//   chest sounded clear." and "Your temperature was normal." on two lines, What happens next "Rest, drink plenty of
//   fluids, and call us if the cough lasts more than two weeks."; the note's Subjective "Synthetic subjective: dry cough,
//   quartzwing pattern, nocturnal.".
// Steps:
//   1. Open the first signed note, then "Write visit summary".
//   2. Type the three parts and select "Release visit summary", with a forged Host header.
//   3. Release a summary of the second note.
//   4. A day passes. Release a summary of the third note.
// Expected result: Step 1: the note's page says "None yet." and offers the link; the form's three fields are
//   empty and the note's Subjective is under it; opening it is one recorded read of the note. Step 2: the note's page
//   says "Released. The patient can now read the summary in the portal." and shows the three parts, line breaks kept,
//   "Released to the patient by Dr. Test" and the time labeled UTC, and no link to write another; the chart tags the
//   note "Summary released"; the database has it as released by the clinician, standing, not sensitive, with a write
//   audit event; the patient gets one email, "Something new in your patient portal", whose text and button
//   ("See what's new", https://localhost/portal/summaries) say nothing of the summary, the note, the clinician or the
//   forged host. Step 3: no second email that day. Step 4: one more email.
// Creator: Tahseen Arnan
public class TC_UC14_18_VisitSummarySuccessfulFlow(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_summary_written_from_a_signed_note_is_released_and_the_patient_is_told_with_no_health_details()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        var (email, _) = await apps.CreatePatientAccountAsync(patient);
        var note = await SignedNoteAsync(session.Client, patient, subjective: NoteSubjective);
        var second = await SignedNoteAsync(session.Client, patient, subjective: NoteSubjective);
        var third = await SignedNoteAsync(session.Client, patient, subjective: NoteSubjective);

        var notePage = await session.Client.GetStringAsync(NotePath(patient, note), Cancel);
        Assert.Contains("None yet. The patient can't see anything of this note.", Words(notePage));
        Assert.Contains("data-write-summary", notePage);
        var readsBefore = await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient);
        var form = await session.Client.GetStringAsync(SummaryPath(patient, note), Cancel);
        // Nothing prefilled: the note is under the form for reference, and each field starts empty.
        foreach (var field in new[] { "summary-talked-about", "summary-found", "summary-next-steps" })
        {
            Assert.Matches($"<textarea id=\"{field}\"[^>]*></textarea>", form);
        }

        Assert.Contains(NoteSubjective, form);
        Assert.Contains("Not copied into the summary.", form);
        // The banner's read of the patient and the note's.
        Assert.Equal(readsBefore + 2, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient));

        // Released, with the request naming another site as its host: the email's link is the configured portal's.
        using var page = new HttpRequestMessage(HttpMethod.Get, SummaryPath(patient, note));
        page.Headers.Host = "attacker.example";
        using var shown = await session.Client.SendAsync(page, Cancel);
        using var post = new HttpRequestMessage(HttpMethod.Post, SummaryPath(patient, note));
        post.Headers.Host = "attacker.example";
        post.Content = Form(("__RequestVerificationToken", Token(await shown.Content.ReadAsStringAsync(Cancel))),
            ("TalkedAbout", TalkedAbout), ("Found", Found.Replace("\n", "\r\n", StringComparison.Ordinal)), ("NextSteps", NextSteps));
        using var released = await session.Client.SendAsync(post, Cancel);
        var released1 = await ReadAsync(released);

        Assert.Equal(NotePath(patient, note), PathOf(released));
        Assert.Contains("Released. The patient can now read the summary in the portal.", Words(released1));
        Assert.Contains(TalkedAbout, released1);
        // Line breaks kept (written as entities in the markup).
        Assert.Contains("Your chest sounded clear.\nYour temperature was normal.", Words(released1));
        Assert.Contains(NextSteps, released1);
        Assert.Contains("Released to the patient by Dr. Test", Words(released1));
        Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2} UTC</time>", released1);
        Assert.DoesNotContain("data-write-summary", released1);
        Assert.Contains("data-withdraw-summary", released1);
        Assert.Equal([$"{clinician.Id} false standing {TalkedAbout}"], await SummariesAsync(apps, patient));
        Assert.Equal([clinician.Id.ToString()], await ProxyTests.QueryAsync(apps,
            "SELECT actor_id::text FROM ehr.audit_events WHERE patient_id = @patient AND table_name = 'visit_summaries' AND action = 'insert'", patient));
        Assert.Contains("data-summary-released", await session.Client.GetStringAsync($"/patients/{patient}", Cancel));

        var notice = Assert.Single(EmailsTo(apps, email));
        Assert.Equal("Something new in your patient portal", notice.Subject);
        Assert.Equal("Your clinic added something to your patient portal. For your privacy it isn't included in this email. Sign in to the patient portal to see it.", notice.Body);
        var action = apps.Emails.Actions[notice.Body];
        Assert.Equal(("See what's new", Base + "portal/summaries"), (action.Label, action.Link));
        foreach (var word in new[] { "cough", "chest", "fluids", "summary", "Dr. Test", "quartzwing", "attacker" })
        {
            Assert.DoesNotContain(word, notice.Subject + notice.Body + action.Link, StringComparison.OrdinalIgnoreCase);
        }

        // A second summary the same day: no second email.
        using (var again = await ReleaseAsync(session.Client, patient, second, "We talked about your sleep."))
        {
            Assert.Contains("Released.", Words(await ReadAsync(again)));
        }

        Assert.Single(EmailsTo(apps, email));

        // A day later (the summaries released more than a day ago, as the superuser moves them without the triggers
        // that would refuse it): the next one is told again.
        await using (var db = await apps.OpenDatabaseAsync())
        {
            await using var command = new NpgsqlCommand(
                "SET session_replication_role = replica; UPDATE ehr.visit_summaries SET released_at = released_at - interval '25 hours' WHERE patient_id = @patient; SET session_replication_role = origin;", db);
            command.Parameters.AddWithValue("patient", patient);
            await command.ExecuteNonQueryAsync(Cancel);
        }

        using (var later = await ReleaseAsync(session.Client, patient, third, "We talked about your follow-up."))
        {
            Assert.Contains("Released.", Words(await ReadAsync(later)));
        }

        Assert.Equal(2, EmailsTo(apps, email).Count);
    }
}
