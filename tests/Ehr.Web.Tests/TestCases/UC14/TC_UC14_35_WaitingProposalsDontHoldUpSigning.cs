using System.Net;
using Ehr.Data.Accounts;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.DictationProposalTests;
using static Ehr.Web.Tests.RefillRequestTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-35
// Purpose / scenario: The orders and allergies a dictation proposed (step 15) hold up neither its review nor signing the
//   note, and signing doesn't decide them: the note being written and the signed note tell their author how many wait,
//   with the way to the review, where each is still opened in its form or dismissed. Nobody else is told.
// Type: Functional (alternative flow)
// Requirements: FR-14 (reworded), FR-31
// Use case: UC 14, Edit and sign a clinical note
// Preconditions: Dr. Test has the patient on their panel, signed in on the on-site app, and dictated a note whose review
//   proposes an order and an allergy to add; a colleague has the patient on their panel too.
// Inputs and system conditions: GET the note being written; the review's Decide and Complete; the editor's Review and
//   confirm; GET the signed note and its review; the review's DismissProposal; on the on-site app.
// Test data: the stub's dictation, each proposed field accepted.
// Steps:
//   1. Open the note being written.
//   2. Accept each field, complete the review, sign the note, and open it and its review.
//   3. Dismiss the order's proposal, and open the note again; the colleague opens it.
// Expected result: Step 1: "Awaiting review: 2 orders or allergies proposed from dictation. Review them. They don't hold
//   up signing the note." with the way to the review. Step 2: the note is signed with both proposals waiting, and its page
//   says the same; the review says "The note is signed. The review of its fields is read-only. The proposed orders and
//   allergies below can still be opened in their forms or dismissed.", and offers both. Step 3: "Awaiting review: an
//   order or allergy proposed from dictation."; the colleague's page says nothing of them.
// Creator: Tahseen Arnan
public class TC_UC14_35_WaitingProposalsDontHoldUpSigning(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Waiting_proposals_hold_up_neither_the_review_nor_signing_and_the_note_tells_its_author()
    {
        var dictated = await DictateAsync(apps);
        var editor = Said(await dictated.Session.Client.GetStringAsync($"/patients/{dictated.Patient}/notes/drafts/{dictated.Draft}", Cancel));
        Assert.Contains("Awaiting review: 2 orders or allergies proposed from dictation. Review them. They don't hold up signing the note.", editor);

        var note = await SignNoteAsync(apps, dictated);
        var html = await dictated.Session.Client.GetStringAsync(note, Cancel);
        Assert.Contains("Awaiting review: 2 orders or allergies proposed from dictation.", Said(html));
        Assert.Contains($"/notes/drafts/{dictated.Draft}/review/{dictated.Review}", html);
        Assert.Equal("pending|-|-", await StateAsync(apps, dictated.Order));
        var review = await dictated.Session.Client.GetStringAsync(dictated.ReviewPath, Cancel);
        Assert.Contains("The note is signed. The review of its fields is read-only. The proposed orders and allergies below can still be opened in their forms or dismissed.", Said(review));
        Assert.NotEmpty(LinkTo(review.Replace("&amp;", "&", StringComparison.Ordinal), OrderFormPath(dictated.Patient, dictated.Order)));
        Assert.Equal(2, CountOf(review, "Dismiss proposal"));

        using (var dismissed = await DismissAsync(dictated, dictated.Order))
        {
            Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);
        }

        Assert.Contains("Awaiting review: an order or allergy proposed from dictation.", Said(await dictated.Session.Client.GetStringAsync(note, Cancel)));

        var colleague = await apps.CreateStaffAsync("Dr. Colleague", StaffRoles.Clinician);
        await apps.AssignAsync(colleague, dictated.Patient);
        var theirs = await SignInAsync(apps.OnPrem, colleague);
        Assert.DoesNotContain("proposed from dictation", Said(await theirs.Client.GetStringAsync(note, Cancel)));
    }
}
