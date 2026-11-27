using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-23
// Purpose / scenario: Off-site, a note's visit summaries are read from the cloud copy, and releasing and withdrawing one
//   are forwarded to the clinic's server. While that server can't be reached they are still read, neither is offered,
//   and a release posted anyway is not saved, with what was typed shown again to copy.
// Type: Functional (exceptional condition)
// Requirements: FR-15, proposed FR-55
// Use case: UC 14, Review and sign a clinical note
// Preconditions: The clinician is signed in with the patient on their panel and has signed a note. The off-site app
//   can reach the clinic's server; a second off-site app can't.
// Inputs and system conditions: GET the note, POST .../summary and .../summaries/{summary}/withdraw on the off-site
//   app; then GET the note, the summary page and the withdraw page, and POST .../summary, on the one cut off.
// Test data: The test's summary; a second one, "We talked about it during the outage.".
// Steps:
//   1. Off-site, release a summary of the note, withdraw it, and release it again.
//   2. On the app that is cut off, open the note, its summary page and the standing summary's withdraw page, and post a
//      second summary as the browser would.
// Expected result: Step 1: each post is forwarded and saved once, under the clinician. Step 2: the note shows the
//   standing summary and the withdrawn one, with "A summary can't be released until the clinic's server is back." and
//   no link to write or withdraw; the summary page has the same sentence and no form; the withdraw page says "A summary
//   can't be withdrawn until the clinic's server is back." and has no form; the post is answered 503, shows the text
//   typed, and saves nothing.
// Creator: Tahseen Arnan
public class TC_UC14_23_VisitSummaryOffSite(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Off_site_releasing_and_withdrawing_are_forwarded_and_with_the_server_down_neither_is_offered()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        var note = await SignedNoteAsync(session.Client, patient);
        var cloud = Client(apps.Cloud, session.Jar);

        Assert.Contains("data-write-summary", await cloud.GetStringAsync(NotePath(patient, note), Cancel));
        using (var released = await ReleaseAsync(cloud, patient, note, "We talked about it off-site."))
        {
            Assert.Contains("Released.", Words(await ReadAsync(released)));
        }

        Assert.Contains($"POST {SummaryPath(patient, note)}", apps.Forwarded);
        var first = await StandingAsync(apps, patient);
        using (var withdrawn = await WithdrawAsync(cloud, patient, note, first))
        {
            Assert.Contains("Withdrawn.", Words(await ReadAsync(withdrawn)));
        }

        Assert.Contains($"POST {WithdrawPath(patient, note, first)}", apps.Forwarded);
        using (var again = await ReleaseAsync(cloud, patient, note))
        {
            Assert.Contains("Released.", Words(await ReadAsync(again)));
        }

        Assert.Equal([$"{clinician.Id} false withdrawn We talked about it off-site.", $"{clinician.Id} false standing {TalkedAbout}"], await SummariesAsync(apps, patient));
        var standing = await StandingAsync(apps, patient);
        var token = Token(await session.Client.GetStringAsync(SummaryPath(patient, note).Replace("/summary", $"/summaries/{standing}/withdraw", StringComparison.Ordinal), Cancel));

        // The cut-off app learns the clinic's server is down from its audit shipper: make it look now (TC-UC14-10).
        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<Ehr.Web.CloudAudit.CloudAuditShipper>().ShipAsync(Cancel));
        var cutOff = Client(apps.CloudCutOff, session.Jar);
        var readOnly = await cutOff.GetStringAsync(NotePath(patient, note), Cancel);
        Assert.Contains(TalkedAbout, readOnly);
        Assert.Contains("<s>We talked about it off-site.</s>", readOnly);
        Assert.Contains("A summary can't be released until the clinic's server is back.", Words(readOnly));
        Assert.DoesNotContain("data-write-summary", readOnly);
        Assert.DoesNotContain("data-withdraw-summary", readOnly);
        var form = await cutOff.GetStringAsync(SummaryPath(patient, note), Cancel);
        Assert.Contains("A summary can't be released until the clinic's server is back.", Words(form));
        Assert.DoesNotContain("data-release-summary", form);
        var withdrawForm = await cutOff.GetStringAsync(WithdrawPath(patient, note, standing), Cancel);
        Assert.Contains("A summary can't be withdrawn until the clinic's server is back.", Words(withdrawForm));
        Assert.DoesNotContain("data-withdraw-summary", withdrawForm);

        // As the browser sends its own form (Sec-Fetch-Site), which is what lets the page show the text again.
        using var request = new HttpRequestMessage(HttpMethod.Post, SummaryPath(patient, note))
        {
            Content = Form(("__RequestVerificationToken", token), ("TalkedAbout", "We talked about it during the outage."), ("Found", "Found."), ("NextSteps", "Next.")),
        };
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        using var notSaved = await cutOff.SendAsync(request, Cancel);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notSaved.StatusCode);
        Assert.Contains("We talked about it during the outage.", await notSaved.Content.ReadAsStringAsync(Cancel));

        Assert.Equal(2, (await SummariesAsync(apps, patient)).Count);
    }
}
