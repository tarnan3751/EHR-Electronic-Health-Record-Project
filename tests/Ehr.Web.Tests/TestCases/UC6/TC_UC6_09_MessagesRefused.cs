using System.Net;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.MessageTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-09
// Purpose / scenario: What the portal's messages refuse: an empty message, another patient's conversation, and
//   sending while the clinic's server can't be reached, when the portal says messages can't be sent until it is back.
// Type: Functional (invalid input and exceptional condition: the outage alternative)
// Requirements: FR-18, FR-27, FR-34
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: Two patients with portal accounts, each with one conversation. For the last part, the cloud app's
//   link to the on-site server is down.
// Inputs and system conditions: POST /portal/messages/new and /portal/messages/{thread} on the on-site app; GET and
//   POST the same on the cloud app whose link is down.
// Test data: Subjects "Synthetic: mine" and "Synthetic: theirs". An empty subject and message; a reply of spaces.
// Steps:
//   1. Send a new conversation with no subject and no message; reply to one's own conversation with only spaces.
//   2. Open, and reply to, the other patient's conversation.
//   3. Off-site with the clinic's server unreachable, open "Messages", the conversation, and "Write to your care
//      team"; post a reply that was already on screen.
// Expected result: Step 1: each is answered 422 with "Not sent:" and "Enter a subject." / "Write your message."; no
//   message is added. Step 2: both are answered 404, and the list never shows the other conversation. Step 3: the
//   pages are readable and say "Read-only: messages can't be sent until the clinic's server is back.", with no form;
//   the post is answered 503 and no message is added.
// Creator: Tahseen Arnan
public class TC_UC6_09_MessagesRefused(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_empty_message_another_patients_thread_and_sending_during_an_outage_are_refused()
    {
        var (_, patient, _, portal) = await CareAsync(apps, "Dr. Refused Messages");
        var (_, otherPatient, _, otherPortal) = await CareAsync(apps, "Dr. Other Messages");
        var mine = await StartAsync(portal.Client, "Synthetic: mine", "Synthetic: first message");
        var theirs = await StartAsync(otherPortal.Client, "Synthetic: theirs", "Synthetic: not for anyone else");

        using var empty = await PostFormAsync(portal.Client, "/portal/messages/new", "/portal/messages/new", ("Subject", ""), ("Body", " "));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        var form = await empty.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("Not sent:", form);
        Assert.Contains("Enter a subject.", form);
        Assert.Contains("Write your message.", form);
        using var blank = await PostFormAsync(portal.Client, $"/portal/messages/{mine}", $"/portal/messages/{mine}", ("Body", "   "));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.StatusCode);

        using var peek = await portal.Client.GetAsync($"/portal/messages/{theirs}", Cancel);
        Assert.Equal(HttpStatusCode.NotFound, peek.StatusCode);
        var token = Token(await portal.Client.GetStringAsync($"/portal/messages/{mine}", Cancel));
        using var intrude = await portal.Client.PostAsync($"/portal/messages/{theirs}", Form(("__RequestVerificationToken", token), ("Body", "Synthetic: intruding")), Cancel);
        Assert.Equal(HttpStatusCode.NotFound, intrude.StatusCode);
        Assert.DoesNotContain("Synthetic: theirs", await portal.Client.GetStringAsync("/portal/messages", Cancel));
        Assert.Single(await RowsAsync(apps, otherPatient, "body"));

        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));
        var cutOff = Client(apps.CloudCutOff, portal.Jar);
        foreach (var path in new[] { "/portal/messages", $"/portal/messages/{mine}", "/portal/messages/new" })
        {
            var page = await cutOff.GetStringAsync(path, Cancel);
            Assert.Contains("Read-only: messages can't be sent until the clinic's server is back.", Words(page));
            Assert.DoesNotContain("<textarea", page);
        }

        Assert.Contains("Synthetic: first message", await cutOff.GetStringAsync($"/portal/messages/{mine}", Cancel));
        using var notSent = await cutOff.PostAsync($"/portal/messages/{mine}", Form(("__RequestVerificationToken", token), ("Body", "Synthetic: typed during the outage")), Cancel);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notSent.StatusCode);
        Assert.Single(await RowsAsync(apps, patient, "body"));
    }
}
