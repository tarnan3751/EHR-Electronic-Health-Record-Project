using System.Net;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-20
// Purpose / scenario: Who can't release a visit summary, and what can't be released: a clinician reading the chart
//   under emergency access (off their panel) is offered nothing and sent back; a note of another patient isn't found
//   through this patient's address; and while a note has a summary standing in the portal, a second one is not offered
//   and not released.
// Type: Functional (alternative flow and invalid input)
// Requirements: FR-31, proposed FR-55
// Use case: UC 14, Review and sign a clinical note
// Preconditions: A clinician with the patient on their panel has signed a note and released a summary of it; another
//   patient on their panel has a signed note. A second clinician, without the patient on their panel, is signed in.
// Inputs and system conditions: GET and POST /patients/{id}/notes/{note}/summary and .../summaries/{summary}/withdraw,
//   on the on-site app.
// Test data: Reason for emergency access "Covering the night shift".
// Steps:
//   1. The second clinician gives a reason for emergency access, opens the note, the summary page and the withdraw
//      page, and posts both.
//   2. The first clinician opens the other patient's note through this patient's address.
//   3. The first clinician opens the summary page of the note that has one, and posts another summary.
// Expected result: Step 1: the note and its summary are shown, with "To write a visit summary, add this patient to
//   My patients." and neither link; each page and post sends them back to the note; nothing changes. Step 2: 404.
//   Step 3: the page says "Already released." and has no form; the post is answered 409 with "A summary of this note has
//   already been released. Withdraw it first to write another."; there is still one summary.
// Creator: Tahseen Arnan
public class TC_UC14_20_VisitSummaryRefused(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Off_the_panel_another_patients_note_and_a_second_standing_summary_are_refused()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        var note = await SignedNoteAsync(session.Client, patient);
        using (var released = await ReleaseAsync(session.Client, patient, note))
        {
            Assert.Contains("Released.", Words(await ReadAsync(released)));
        }

        var summary = await StandingAsync(apps, patient);
        var other = await apps.InsertPatientAsync("Synthetic", "Elsewhere");
        await apps.AssignAsync(clinician, other);
        var otherNote = await SignedNoteAsync(session.Client, other);

        // Emergency access: read, and nothing offered or done.
        var covering = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Dr. Covering Summaries", Ehr.Data.Accounts.StaffRoles.Clinician));
        using var opened = await PostFormAsync(covering.Client, $"/patients/{patient}/reason", $"/patients/{patient}/reason", ("Reason", "Covering the night shift"));
        var token = Token(await ReadAsync(opened));
        var read = await covering.Client.GetStringAsync(NotePath(patient, note), Cancel);
        Assert.Contains(TalkedAbout, read);
        Assert.Contains("To write a visit summary, add this patient to My patients.", Words(read));
        Assert.DoesNotContain("data-write-summary", read);
        Assert.DoesNotContain("data-withdraw-summary", read);
        var noRedirects = Client(apps.OnPrem, covering.Jar, followRedirects: false);
        foreach (var path in new[] { SummaryPath(patient, note), WithdrawPath(patient, note, summary) })
        {
            using var page = await noRedirects.GetAsync(path, Cancel);
            Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
            Assert.Equal(NotePath(patient, note), page.Headers.Location?.OriginalString);
            using var posted = await noRedirects.PostAsync(path, Form(("__RequestVerificationToken", token), ("TalkedAbout", "Off the panel."), ("Found", "Off the panel."),
                ("NextSteps", "Off the panel."), ("Reason", "Off the panel.")), Cancel);
            Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);
        }

        // Another patient's note, and a summary of another note, through this patient's address: not found.
        using (var elsewhere = await session.Client.GetAsync(SummaryPath(patient, otherNote), Cancel))
        {
            Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);
        }

        using (var mismatched = await session.Client.GetAsync(WithdrawPath(other, otherNote, summary), Cancel))
        {
            Assert.Equal(HttpStatusCode.NotFound, mismatched.StatusCode);
        }

        // A second standing summary of the same note.
        var form = await session.Client.GetStringAsync(SummaryPath(patient, note), Cancel);
        Assert.Contains("Already released. A summary of this note was released by Dr. Test", Words(form));
        Assert.DoesNotContain("data-release-summary", form);
        using var second = await PostFormAsync(session.Client, SummaryPath(patient, note), NotePath(patient, note),
            ("TalkedAbout", "A second summary."), ("Found", "Found."), ("NextSteps", "Next."));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("A summary of this note has already been released. Withdraw it first to write another.", Words(await second.Content.ReadAsStringAsync(Cancel)));

        Assert.Equal([$"{clinician.Id} false standing {TalkedAbout}"], await SummariesAsync(apps, patient));
        Assert.Empty(await SummariesAsync(apps, other));
    }
}
