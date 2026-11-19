using System.Net;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-10
// Purpose / scenario: Off-site, a signed note and its addenda are read from the cloud copy and a new addendum is
//   forwarded to the clinic's server; while that server can't be reached the note and addenda are still read, and
//   signing an addendum is not offered.
// Type: Functional (exceptional condition)
// Requirements: FR-31, FR-14, FR-15, proposed FR-49
// Use case: UC 14, Review and sign a clinical note
// Preconditions: The clinician is signed in with the patient on their panel and has signed a note. The off-site app
//   can reach the clinic's server; a second off-site app can't.
// Inputs and system conditions: GET the note and POST .../addendum on the off-site app, then on the one cut off.
// Test data: "Synthetic addendum signed off-site."
// Steps:
//   1. Off-site, open the note, follow "Add an addendum" and sign the text.
//   2. On the app that is cut off, open the note, and post an addendum to its addendum page.
// Expected result: Step 1: the post is forwarded and the addendum is saved once, under the clinician. Step 2: the
//   note and the addendum are shown, with "An addendum can't be signed until the clinic's server is back." and no
//   link; the post is answered 503 and nothing more is saved.
// Creator: Tahseen Arnan
public class TC_UC14_10_AddendumOffSite(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Off_site_an_addendum_is_forwarded_and_with_the_server_down_the_note_is_read_only()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        var note = await SignedNoteAsync(session.Client, patient);
        var cloud = Client(apps.Cloud, session.Jar);

        Assert.Contains("data-add-addendum", await cloud.GetStringAsync(NotePath(patient, note), Cancel));
        using var signed = await AddAsync(cloud, patient, note, "Synthetic addendum signed off-site.");
        Assert.Contains($"POST {AddendumPath(patient, note)}", apps.Forwarded);
        Assert.Equal([$"{clinician.Id} false Synthetic addendum signed off-site."], await AddendaAsync(apps, patient));
        var token = Token(await session.Client.GetStringAsync(AddendumPath(patient, note), Cancel));

        var cutOff = Client(apps.CloudCutOff, session.Jar);
        var readOnly = await cutOff.GetStringAsync(NotePath(patient, note), Cancel);
        Assert.Contains(History, readOnly);
        Assert.Contains("Synthetic addendum signed off-site.", readOnly);
        Assert.Contains("An addendum can't be signed until the clinic's server is back.", readOnly);
        Assert.DoesNotContain("data-add-addendum", readOnly);
        using var notSaved = await cutOff.PostAsync(AddendumPath(patient, note), Form(("__RequestVerificationToken", token), ("Text", "Synthetic addendum during the outage.")), Cancel);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notSaved.StatusCode);

        Assert.Single(await AddendaAsync(apps, patient));
    }
}
