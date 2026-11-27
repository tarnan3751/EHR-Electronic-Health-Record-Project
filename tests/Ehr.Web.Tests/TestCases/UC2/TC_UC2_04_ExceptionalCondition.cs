using System.Net;
using System.Text;
using Ehr.Web.Api.Audit;
using Ehr.Web.CloudAudit;
using Ehr.Web.Forwarding;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC2;

// Test Case ID: TC-UC2-04
// Purpose / scenario: The on-site app still answers, but its database is down. An answer from it must not be taken
//   as the outage being over: the site stays read-only.
// Type: Functional (exceptional condition)
// Requirements: FR-34
// Use case: UC 2, Read records during an on-site outage
// Preconditions: The clinician signed in off-site while the on-site server was up. The on-site app's database is then
//   down while the app itself still answers (PrimaryDown on the CloudPrimaryDrill app), and the cloud app's check has
//   found that out.
// Inputs and system conditions: On the cloud app: a POST to /api/audit/cloud-events without the shared key, from
//   nobody signed in, which is forwarded to the on-site app and answered by it; then GET /quick-texts as the
//   clinician.
// Test data: The POST's body is "[]" (no events), as application/json.
// Steps:
//   1. With the on-site database down, send the POST without the key through the cloud app.
//   2. Read the cloud app's record of whether the on-site server can be reached.
//   3. Open the quick texts page off-site as the clinician.
// Expected result: The on-site app answers the POST 401. The cloud app still holds the on-site server as not
//   reachable, and the page shows "Read-only. The clinic's server can't be reached".
// Creator: Tahseen Arnan
public class TC_UC2_04_ExceptionalCondition(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task During_an_outage_an_answer_the_on_prem_app_still_gives_leaves_the_site_read_only()
    {
        var signedIn = await CookieHeaderAsync(apps.Cloud, await apps.ClinicianAsync());
        apps.PrimaryDown = true;
        try
        {
            Assert.False(await apps.CloudPrimaryDrill.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));

            using var client = CreateClient(apps.CloudPrimaryDrill, handleCookies: false);
            // Anyone on the internet: a POST without the key, which the cloud app forwards and the on-prem app refuses
            // without its database.
            using (var refused = await client.PostAsync(CloudAuditApi.Path, new StringContent("[]", Encoding.UTF8, "application/json"), Cancel))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            }

            Assert.False(apps.CloudPrimaryDrill.Services.GetRequiredService<OnPremStatus>().IsReachable);
            using var request = new HttpRequestMessage(HttpMethod.Get, QuickTexts);
            request.Headers.Add("Cookie", signedIn);
            using var page = await client.SendAsync(request, Cancel);
            Assert.Contains("Read-only. The clinic's server can't be reached", Words(await ReadAsync(page)));
        }
        finally
        {
            apps.PrimaryDown = false;
        }
    }
}
