using System.Net;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-15
// Purpose / scenario: Changing a note's sensitivity needs the patient on the clinician's panel; off-site it is
//   forwarded to the clinic's server, and while that server can't be reached it isn't offered.
// Type: Functional (exceptional condition; alternative flow)
// Requirements: FR-31, FR-02, FR-14, FR-15, proposed FR-51
// Use case: UC 14, Review and sign a clinical note
// Preconditions: The clinician is signed in with the patient on their panel and has signed a note. A covering
//   clinician has opened the chart with a reason. The off-site app can reach the clinic's server; a second can't.
// Inputs and system conditions: GET and POST /patients/{id}/notes/{note}/sensitivity on each app.
// Test data: Reason "Patient asked for it."
// Steps:
//   1. As the covering clinician, open the note, open the page, and post to it.
//   2. Off-site, mark the note sensitive.
//   3. On the app that is cut off, open the note and the page, and post to it.
// Expected result: Step 1: the note has no "Mark this note sensitive" link; the page and the post send them back to
//   the note; nothing changes. Step 2: forwarded, and the note is sensitive, marked by the clinician. Step 3: the
//   note has no link; the page says "This can't be changed until the clinic's server is back." with no form; the post
//   is answered 503; the note is still sensitive.
// Creator: Tahseen Arnan
public class TC_UC14_15_SensitivityRefusedAndOffSite(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sensitivity_changes_need_the_panel_and_follow_the_off_site_rules()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        var note = await SignedNoteAsync(session.Client, patient);

        var covering = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Dr. Covering Sensitivity", Ehr.Data.Accounts.StaffRoles.Clinician));
        using var opened = await PostFormAsync(covering.Client, $"/patients/{patient}/reason", $"/patients/{patient}/reason", ("Reason", "Covering the night shift"));
        var token = Token(await ReadAsync(opened));
        Assert.DoesNotContain("data-note-sensitivity", await covering.Client.GetStringAsync(NotePath(patient, note), Cancel));
        var noRedirects = Client(apps.OnPrem, covering.Jar, followRedirects: false);
        using var page = await noRedirects.GetAsync(SensitivityPath(patient, note), Cancel);
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal(NotePath(patient, note), page.Headers.Location?.OriginalString);
        using var posted = await noRedirects.PostAsync(SensitivityPath(patient, note), Form(("__RequestVerificationToken", token), ("Reason", "Patient asked for it.")), Cancel);
        Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);
        Assert.Equal(["note false none none"], await SensitivityAsync(apps, patient));

        var cloud = Client(apps.Cloud, session.Jar);
        using var marked = await ChangeSensitivityAsync(cloud, patient, note, "Patient asked for it.");
        Assert.Contains($"POST {SensitivityPath(patient, note)}", apps.Forwarded);
        Assert.Equal([$"note true {clinician.Id} Patient asked for it."], await SensitivityAsync(apps, patient));
        var own = Token(await session.Client.GetStringAsync(SensitivityPath(patient, note), Cancel));

        var cutOff = Client(apps.CloudCutOff, session.Jar);
        Assert.DoesNotContain("data-note-sensitivity", await cutOff.GetStringAsync(NotePath(patient, note), Cancel));
        var readOnly = await cutOff.GetStringAsync(SensitivityPath(patient, note), Cancel);
        Assert.Contains("This can't be changed until the clinic's server is back.", readOnly);
        Assert.DoesNotContain("data-change-sensitivity", readOnly);
        using var notSaved = await cutOff.PostAsync(SensitivityPath(patient, note), Form(("__RequestVerificationToken", own), ("Reason", "During the outage.")), Cancel);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notSaved.StatusCode);
        Assert.Equal([$"note true {clinician.Id} Patient asked for it."], await SensitivityAsync(apps, patient));
    }
}
