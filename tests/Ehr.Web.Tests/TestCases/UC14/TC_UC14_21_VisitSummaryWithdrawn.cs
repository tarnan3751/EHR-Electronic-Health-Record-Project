using System.Net;
using Ehr.Data.Accounts;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-21
// Purpose / scenario: A wrong visit summary is withdrawn with a reason, never edited: a colleague on the patient's
//   care team withdraws it, it stays on the note's page struck through with who withdrew it, when and why, and a new
//   one can then be written. It is withdrawn once. The clinician who released it gets an inbox item with no text
//   (Tahseen's decision of 21 November 2026); a summary nobody viewed in the portal emails nobody (the email after a view
//   is TC-UC6-17).
// Type: Functional (alternative flow: withdrawing a summary)
// Requirements: proposed FR-55
// Use case: UC 14, Review and sign a clinical note
// Preconditions: A clinician with the patient on their panel has signed a note and released a summary of it. A
//   colleague, Dr. Colleague, also has the patient on their panel. The patient has a portal account.
// Inputs and system conditions: GET and POST /patients/{id}/notes/{note}/summaries/{summary}/withdraw, then the
//   note's page and its summary page, on the on-site app.
// Test data: Reason "Written for the wrong visit."; the new summary "We talked about the right visit.".
// Steps:
//   1. Dr. Colleague selects "Withdraw this summary…" and posts it with no reason, then with the reason.
//   2. They write and release a new summary of the note.
//   3. They post the withdrawal of the first summary again.
// Expected result: Step 1: with no reason, 422 and "Not withdrawn. Say why, so the record shows it."; with it, the
//   note's page says "Withdrawn. The patient no longer sees the summary in the portal.", shows the summary struck through
//   with "Withdrawn by Dr. Colleague" and the reason, and offers "Write visit summary" again. Step 2: the
//   new one is the standing one, released by Dr. Colleague. Step 3: the page says "Already withdrawn." with no form,
//   and the post is answered 409; the database has the first withdrawn by Dr. Colleague with the reason, and the
//   second standing. Dr. Test's inbox has "A visit summary you released was withdrawn" with a link to the note and
//   neither the summary nor the reason; Dr. Colleague's has nothing for it. Only the first release emailed the patient.
// Creator: Tahseen Arnan
public class TC_UC14_21_VisitSummaryWithdrawn(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_wrong_summary_is_withdrawn_with_a_reason_by_a_colleague_and_a_new_one_written()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        var (email, _) = await apps.CreatePatientAccountAsync(patient);
        var note = await SignedNoteAsync(session.Client, patient);
        using (var released = await ReleaseAsync(session.Client, patient, note))
        {
            Assert.Contains("Released.", Words(await ReadAsync(released)));
        }

        var first = await StandingAsync(apps, patient);
        var colleague = await apps.CreateStaffAsync("Dr. Colleague", StaffRoles.Clinician);
        await apps.AssignAsync(colleague, patient);
        var other = await SignInAsync(apps.OnPrem, colleague);

        var notePage = await other.Client.GetStringAsync(NotePath(patient, note), Cancel);
        Assert.Contains($"href=\"{WithdrawPath(patient, note, first)}\"", notePage);
        var form = await other.Client.GetStringAsync(WithdrawPath(patient, note, first), Cancel);
        Assert.Contains(TalkedAbout, form);
        Assert.Contains("will no longer see its text in the portal", Words(form));
        using (var noReason = await WithdrawAsync(other.Client, patient, note, first, "  "))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
            Assert.Contains("Not withdrawn. Say why, so the record shows it.", Words(await noReason.Content.ReadAsStringAsync(Cancel)));
        }

        using var withdrawn = await WithdrawAsync(other.Client, patient, note, first);
        var page = await ReadAsync(withdrawn);
        Assert.Equal(NotePath(patient, note), PathOf(withdrawn));
        Assert.Contains("Withdrawn. The patient no longer sees the summary in the portal.", Words(page));
        Assert.Contains("data-summary=\"withdrawn\"", page);
        Assert.Contains($"<s>{TalkedAbout}</s>", page);
        Assert.Matches(@"Withdrawn by Dr. Colleague · [A-Z][a-z]{2} \d{1,2}, \d{4} \d{2}:\d{2} UTC: Written for the wrong visit\.", Words(page));
        Assert.Contains("data-write-summary", page);
        Assert.DoesNotContain("data-withdraw-summary", page);

        using (var again = await ReleaseAsync(other.Client, patient, note, "We talked about the right visit."))
        {
            var replaced = await ReadAsync(again);
            Assert.Contains("data-summary=\"standing\"", replaced);
            Assert.Contains("Released to the patient by Dr. Colleague", Words(replaced));
        }

        var twice = await other.Client.GetStringAsync(WithdrawPath(patient, note, first), Cancel);
        Assert.Contains("Already withdrawn. Withdrawn by Dr. Colleague", Words(twice));
        Assert.DoesNotContain("data-withdraw-summary", twice);
        using (var posted = await PostFormAsync(other.Client, WithdrawPath(patient, note, first), NotePath(patient, note), ("Reason", "Again.")))
        {
            Assert.Equal(HttpStatusCode.Conflict, posted.StatusCode);
        }

        Assert.Equal([$"{clinician.Id} false withdrawn {TalkedAbout}", $"{colleague.Id} false standing We talked about the right visit."], await SummariesAsync(apps, patient));
        Assert.Equal([$"{colleague.Id} Written for the wrong visit."], await ProxyTests.QueryAsync(apps,
            "SELECT withdrawn_by || ' ' || withdrawn_reason FROM ehr.visit_summaries WHERE patient_id = @patient AND withdrawn_at IS NOT NULL", patient));
        // Only the first release emailed the patient: the withdrawn one was never viewed in the portal, and the second
        // release came the same day.
        Assert.Single(EmailsTo(apps, email));

        // The releaser is told, in their inbox, with nothing of the summary or the reason; the colleague isn't.
        var inbox = await session.Client.GetStringAsync("/inbox", Cancel);
        Assert.Contains("Visit summary withdrawn A visit summary you released was withdrawn", Words(inbox));
        Assert.Contains($"href=\"{NotePath(patient, note)}#summary\"", inbox);
        Assert.DoesNotContain("wrong visit", inbox);
        Assert.DoesNotContain(TalkedAbout, inbox);
        Assert.DoesNotContain("data-inbox-item=\"summary-withdrawn\"", await other.Client.GetStringAsync("/inbox", Cancel));
    }
}
