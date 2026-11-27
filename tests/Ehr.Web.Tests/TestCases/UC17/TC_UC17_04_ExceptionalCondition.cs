using System.Net;
using Ehr.Data.Audit;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.MessageTests;

namespace Ehr.Web.Tests.TestCases.UC17;

// Test Case ID: TC-UC17-04
// Purpose / scenario: Off-site, a clinician reads a conversation from the cloud copy, which is recorded through the
//   cloud app's audit queue, and a reply is forwarded to the clinic's server. With the clinic's server unreachable the
//   conversation stays readable, the reply form is replaced by a sentence saying why, and a reply already on screen
//   isn't sent, but is shown again to copy.
// Type: Functional (exceptional condition)
// Requirements: FR-18, FR-33, FR-34, proposed FR-43
// Use case: UC 17, Answer a patient's message
// Preconditions: The clinician is signed in with the patient on their panel; the patient has sent a message. For the
//   second part, the cloud app's link to the on-site server is down.
// Inputs and system conditions: GET and POST /patients/{id}/messages/{thread} on the cloud app, then on the cloud app
//   whose link is down (a plain form post).
// Test data: Reply "Synthetic: sent from off-site". Then "Synthetic: typed during the outage".
// Steps:
//   1. Off-site, open the conversation and reply.
//   2. With the link down, open the chart and the conversation.
//   3. Post a reply as a plain form.
// Expected result: Step 1: the read arrives on the clinic's server as a read of the thread with source "cloud"; the
//   reply is forwarded and saved. Step 2: the chart says "Messages can't be sent until the clinic's server is back.";
//   the conversation shows both messages and "Messages can't be sent until the clinic's server is back. The
//   conversation remains readable.", with no form. Step 3: answered 503 with the "Not saved" page showing the typed
//   reply; no message is added.
// Creator: Tahseen Arnan
public class TC_UC17_04_ExceptionalCondition(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Off_site_a_reply_is_forwarded_and_with_the_server_down_the_thread_is_read_only()
    {
        var (clinician, patient, staff, portal) = await CareAsync(apps, "Dr. Off-site Messages");
        var thread = await StartAsync(portal.Client, "Synthetic: off-site", "Synthetic: are you there?");
        var path = $"/patients/{patient}/messages/{thread}";
        var cloud = Client(apps.Cloud, staff.Jar);

        var page = await cloud.GetStringAsync(path, Cancel);
        Assert.Contains("Synthetic: are you there?", page);
        Assert.True(await apps.Cloud.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));
        Assert.True(await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient, AuditSources.Cloud) >= 1);
        using var reply = await cloud.PostAsync(path, Form(("__RequestVerificationToken", Token(page)), ("Body", "Synthetic: sent from off-site")), Cancel);
        Assert.Contains($"POST {path}", apps.Forwarded);
        Assert.Equal(["Synthetic: are you there?", "Synthetic: sent from off-site"], await RowsAsync(apps, patient, "body"));

        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));
        var cutOff = Client(apps.CloudCutOff, staff.Jar);
        Assert.Contains("Messages can't be sent until the clinic's server is back.", await cutOff.GetStringAsync($"/patients/{patient}", Cancel));
        var readOnly = await cutOff.GetStringAsync(path, Cancel);
        Assert.Contains("Synthetic: sent from off-site", readOnly);
        Assert.Contains("Messages can't be sent until the clinic's server is back. The conversation remains readable.", readOnly);
        Assert.DoesNotContain("<textarea", readOnly);

        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Content = Form(("__RequestVerificationToken", Token(page)), ("Body", "Synthetic: typed during the outage"));
        using var notSent = await cutOff.SendAsync(request, Cancel);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notSent.StatusCode);
        Assert.Contains("Synthetic: typed during the outage", await notSent.Content.ReadAsStringAsync(Cancel));
        Assert.Equal(2, (await RowsAsync(apps, patient, "body")).Count);
    }
}
