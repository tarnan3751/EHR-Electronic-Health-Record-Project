using System.Net;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.InboxTests;

namespace Ehr.Web.Tests.TestCases.UC15;

// Test Case ID: TC-UC15-04
// Purpose / scenario: Off-site, the inbox is read from the cloud copy and "Mark done" is forwarded to the clinic's
//   server. With the clinic's server unreachable, the inbox stays readable, "Mark done" is off the page with a
//   sentence saying why, and a post that was already on screen isn't saved.
// Type: Functional (exceptional condition)
// Requirements: proposed FR-41, FR-33, FR-34
// Use case: UC 15, Work through the inbox
// Preconditions: The clinician is signed in, with two items in their inbox. For the second part, the cloud app's link
//   to the on-site server is down.
// Inputs and system conditions: GET /inbox and POST /inbox?handler=Done on the cloud app; then the same on the cloud
//   app whose link is down, with a form token read before.
// Test data: Clinician "Dr. Inbox Off-site". Items "Synthetic: first off-site item" and "Synthetic: second off-site item".
// Steps:
//   1. Off-site, open the inbox and mark the first item done.
//   2. With the link down, open the inbox.
//   3. Post "Mark done" for the second item.
// Expected result: Step 1: the post is forwarded and the first item is done on the clinic's server. Step 2: the inbox
//   lists the second item with no "Mark done" button and says "Marking an item done needs the clinic's server. Your
//   inbox remains readable."; the app bar shows the count. Step 3: the post is answered 503 and the item still
//   needs action.
// Creator: Tahseen Arnan (expansion step 3); reviewed and run by Samir Mukshe, approving PR 2
public class TC_UC15_04_ExceptionalCondition(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Off_site_marking_done_is_forwarded_and_with_the_server_down_the_inbox_is_read_only()
    {
        var (clinician, patient, session) = await ClinicianAsync(apps, "Dr. Inbox Off-site");
        var first = await AddAsync(apps, clinician.Id, "Synthetic: first off-site item", patient, minutesAgo: 10);
        var second = await AddAsync(apps, clinician.Id, "Synthetic: second off-site item", patient);
        var cloud = Client(apps.Cloud, session.Jar);
        var token = Token(await cloud.GetStringAsync("/inbox", Cancel));

        using var done = await MarkDoneAsync(cloud, first, token);
        Assert.Contains($"POST /inbox?handler=Done&id={first}", apps.Forwarded);
        Assert.True(await IsDoneAsync(apps, first));

        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));
        var cutOff = Client(apps.CloudCutOff, session.Jar);
        var inbox = await cutOff.GetStringAsync("/inbox", Cancel);
        Assert.Contains("Synthetic: second off-site item", inbox);
        Assert.DoesNotContain("Mark done</button>", inbox);
        Assert.Contains("Marking an item done needs the clinic's server. Your inbox remains readable.", inbox);
        Assert.Contains("data-inbox-count", inbox);

        using var refused = await MarkDoneAsync(cutOff, second, token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.False(await IsDoneAsync(apps, second));
    }
}
