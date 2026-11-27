using System.Net;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC1;

// Test Case ID: TC-UC1-04
// Purpose / scenario: Off-site, with the on-site server unreachable, the clinician signs an order. It isn't saved,
//   the page says why, and the form keeps its entries.
// Type: Functional (exceptional condition)
// Requirements: FR-05, FR-33, FR-34
// Use case: UC 1, Place an order
// Preconditions: The clinician signed in while the on-site server was up, and the patient is on their panel. The
//   cloud app's link to the on-site server is then down (the CloudCutOff app), and its check has found that out.
// Inputs and system conditions: GET the chart and the order form on the cloud app; POST the form once as js/orders.js
//   sends it (fetch) and once as a plain form without JavaScript.
// Test data: Patient Synthetic Orderable. Order type Lab, item "Urinalysis", Reason for the test "Synthetic fatigue".
// Steps:
//   1. Open the patient's chart off-site.
//   2. Open the lab order form off-site.
//   3. Sign the order as the script sends it.
//   4. Sign the order as a plain form post.
// Expected result: The chart offers no "New order", under the read-only band. The script's post is answered 503, on which js/orders.js leaves the form on screen with its entries
//   and shows "Not saved. The clinic's server can't be reached. What you entered is still here, to try again once
//   it's back." The plain post is answered 503 with the "Not saved" page: "Not saved. The clinic's server can't be
//   reached." No order is saved.
// Creator: Tahseen Arnan
public class TC_UC1_04_ExceptionalCondition(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Off_site_with_the_clinics_server_down_the_order_is_not_saved()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));
        var cutOff = Client(apps.CloudCutOff, session.Jar);

        var chart = await cutOff.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.DoesNotContain("data-new-order", chart);
        Assert.Contains("sc-notice--band", chart);
        Assert.Contains("/_content/Ehr.Design/js/orders.", await cutOff.GetStringAsync(FormPath(patient, "lab"), Cancel));

        using var fetched = await SignAsync(cutOff, patient, "lab", "Urinalysis", Cbc);
        using var posted = await SignAsync(cutOff, patient, "lab", "Urinalysis", Cbc, fetch: false);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, fetched.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, posted.StatusCode);
        Assert.Contains("Not saved.</span> The clinic's server can't be reached.", await posted.Content.ReadAsStringAsync(Cancel));
        Assert.Equal(0, await OrdersAsync(apps, patient));
    }
}
