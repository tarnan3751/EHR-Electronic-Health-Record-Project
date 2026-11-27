using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-13
// Purpose / scenario: An addendum written by someone other than the note's signer is labeled as such, and the signer
//   finds an item for it in their inbox; the signer's own addendum is not labeled and tells nobody.
// Type: Functional (alternative flow: an addendum by another clinician)
// Requirements: FR-31, proposed FR-49, proposed FR-51
// Use case: UC 14, Review and sign a clinical note (and UC 15, Work through the inbox)
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel and has signed a note.
//   A colleague with the patient on their panel is signed in too.
// Inputs and system conditions: POST /patients/{id}/notes/{note}/addendum as each; GET /inbox as the signer; POST
//   /inbox?handler=Done; POST /api/desktop/offline-panel.
// Test data: The signer's "Synthetic addendum by the signer." and the colleague's "Synthetic addendum by a colleague."
// Steps:
//   1. As the signer, sign an addendum. Open the inbox.
//   2. As the colleague, sign an addendum. As the signer, open the inbox and follow "Open the note".
//   3. As the signer, mark the item done. Download the offline copy.
// Expected result: Step 1: the inbox has nothing for it. Step 2: the signer's inbox has one "Addendum to your note"
//   item, "An addendum was added to a note you signed", with the patient's name and none of the addendum's text; the
//   app bar counts it; the link opens the note, where the colleague's addendum reads "Addendum by Dr. Colleague, not
//   the note's author" and the signer's own has only their name. The colleague's inbox has nothing. Step 3: the item
//   is done; the offline copy marks the colleague's addendum as not by the note's author.
// Creator: Tahseen Arnan
public class TC_UC14_13_AddendumByAnotherClinician(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task An_addendum_by_another_clinician_is_labeled_and_reaches_the_signers_inbox()
    {
        var (signer, patient, session) = await OnPanelAsync(apps);
        var note = await SignedNoteAsync(session.Client, patient);
        var colleague = await apps.CreateStaffAsync("Dr. Colleague", Ehr.Data.Accounts.StaffRoles.Clinician);
        await apps.AssignAsync(colleague, patient);
        var other = await SignInAsync(apps.OnPrem, colleague);

        using var own = await AddAsync(session.Client, patient, note, "Synthetic addendum by the signer.");
        Assert.DoesNotContain("Addendum to your note", await session.Client.GetStringAsync("/inbox", Cancel));

        using var theirs = await AddAsync(other.Client, patient, note, "Synthetic addendum by a colleague.");
        var inbox = await session.Client.GetStringAsync("/inbox", Cancel);
        Assert.Contains("<span class=\"sc-tag\">Addendum to your note</span> An addendum was added to a note you signed", inbox);
        Assert.Matches($"<a [^>]*href=\"{NotePath(patient, note)}#addenda\"[^>]*>Open the note</a>", inbox);
        Assert.Contains("Orderable, Synthetic", inbox);
        Assert.DoesNotContain("Synthetic addendum by a colleague.", inbox);
        Assert.Contains("data-inbox-count", inbox);
        Assert.DoesNotContain("Addendum to your note", await other.Client.GetStringAsync("/inbox", Cancel));

        var page = await session.Client.GetStringAsync(NotePath(patient, note), Cancel);
        Assert.Matches("(?s)<strong>Dr. Test</strong>.*?by the signer\\..*?<strong>Addendum by Dr. Colleague, not the note&#x27;s author</strong>.*?by a colleague\\.", page);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "not the note&#x27;s author"));

        var item = Guid.Parse((await ProxyTests.QueryAsync(apps, "SELECT id::text FROM ehr.inbox_items WHERE patient_id = @patient AND kind = 'addendum'", patient)).Single());
        using var done = await InboxTests.MarkDoneAsync(session.Client, item);
        Assert.DoesNotContain("Addendum to your note", await ReadAsync(done));
        Assert.Equal([$"{signer.Id} true"], await ProxyTests.QueryAsync(apps,
            "SELECT recipient_id || ' ' || (done_at IS NOT NULL) FROM ehr.inbox_items WHERE patient_id = @patient AND kind = 'addendum'", patient));

        var token = await apps.DesktopSignInAsync(apps.OnPrem, signer);
        using var offline = await DesktopSignInTests.PostAsync(apps.OnPrem, "/api/desktop/offline-panel", token);
        var copy = await offline.Content.ReadAsStringAsync(Cancel);
        Assert.Matches("\"author\":\"Dr. Test\",\"text\":\"Synthetic addendum by the signer.\",\"notNoteAuthor\":false", copy);
        Assert.Matches("\"author\":\"Dr. Colleague\",\"text\":\"Synthetic addendum by a colleague.\",\"notNoteAuthor\":true", copy);
    }
}
