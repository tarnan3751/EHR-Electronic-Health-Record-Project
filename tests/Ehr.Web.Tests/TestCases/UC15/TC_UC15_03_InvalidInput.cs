using System.Net;
using Ehr.Data.Accounts;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.InboxTests;

namespace Ehr.Web.Tests.TestCases.UC15;

// Test Case ID: TC-UC15-03
// Purpose / scenario: "Mark done" for an item that isn't the person's own, doesn't exist, or was done already changes
//   nothing, gives nothing away about other people's items, and says the item was done before.
// Type: Functional (invalid input)
// Requirements: proposed FR-41, FR-28
// Use case: UC 15, Work through the inbox
// Preconditions: Two clinicians are signed in on the on-site app. The first has one item; the second has none.
// Inputs and system conditions: GET /inbox, and POST /inbox?handler=Done&id={id} with another person's item, an ID
//   that doesn't exist, text that isn't an ID, and the person's own item twice; on the on-site app. Then GET /inbox
//   as a patient and with nobody signed in.
// Test data: Clinicians "Dr. Inbox Owner" and "Dr. Inbox Other". Item "Synthetic: for the owner only".
// Steps:
//   1. As the second clinician, open the inbox; post "Mark done" for the first clinician's item, and for an ID that
//      doesn't exist.
//   2. As the first clinician, mark the item done, then post the same again.
//   3. Request the inbox as a signed-in patient, and with nobody signed in.
// Expected result: Step 1: the second clinician's inbox doesn't show the item; both posts are answered with the inbox
//   and "Nothing to mark. That item isn't waiting in your inbox. It may already be done."; the item still needs action. Step 2: the
//   first post marks it; the second is answered the same way and the done time doesn't change. Step 3: the patient
//   and the anonymous request are each sent away (to staff sign-in or a "denied" page), and see no inbox.
// Creator: Tahseen Arnan (expansion step 3); reviewed and run by Samir Mukshe, approving PR 2
public class TC_UC15_03_InvalidInput(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Marking_someone_elses_item_or_a_done_one_changes_nothing()
    {
        var (owner, patient, session) = await ClinicianAsync(apps, "Dr. Inbox Owner");
        var item = await AddAsync(apps, owner.Id, "Synthetic: for the owner only", patient);
        var other = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Dr. Inbox Other", StaffRoles.Clinician));

        Assert.DoesNotContain("for the owner only", await other.Client.GetStringAsync("/inbox", Cancel));
        foreach (var id in new[] { item, Guid.NewGuid() })
        {
            using var refused = await MarkDoneAsync(other.Client, id);
            Assert.Contains("Nothing to mark. That item isn't waiting in your inbox. It may already be done.", Words(await ReadAsync(refused)));
        }

        Assert.False(await IsDoneAsync(apps, item));
        var token = Token(await other.Client.GetStringAsync("/inbox", Cancel));
        using var notAnId = await other.Client.PostAsync("/inbox?handler=Done&id=not-an-id", Form(("__RequestVerificationToken", token)), Cancel);
        Assert.NotEqual(HttpStatusCode.InternalServerError, notAnId.StatusCode);

        using var first = await MarkDoneAsync(session.Client, item);
        Assert.True(await IsDoneAsync(apps, item));
        using var again = await MarkDoneAsync(session.Client, item);
        Assert.Contains("Nothing to mark.", Words(await ReadAsync(again)));

        var patientSession = await PortalResultsTests.SignInPatientAsync(apps, apps.OnPrem, patient);
        using var asPatient = await Client(apps.OnPrem, patientSession.Jar, followRedirects: false).GetAsync("/inbox", Cancel);
        // A patient's session is no staff session: sent away, to staff sign-in or the "denied" page, never shown an inbox.
        Assert.Equal(HttpStatusCode.Redirect, asPatient.StatusCode);
        Assert.Matches("/sign-in|/denied", asPatient.Headers.Location!.ToString());
        using var anonymous = await CreateClient(apps.OnPrem).GetAsync("/inbox", Cancel);
        Assert.Contains("/sign-in", anonymous.RequestMessage!.RequestUri!.AbsolutePath + anonymous.Headers.Location);
    }
}
