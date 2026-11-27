using System.Net;
using System.Text.RegularExpressions;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC2;

// Test Case ID: TC-UC2-03
// Purpose / scenario: During the outage the clinician submits a change, which can't be made. It is refused with the
//   "Not saved" page, which says why and shows again what was typed.
// Type: Functional (invalid input)
// Requirements: FR-33, FR-34
// Use case: UC 2, Read records during an on-site outage
// Preconditions: The clinician signed in while the on-site server was up. The cloud app's link to the on-site server
//   is then down (the CloudCutOff app).
// Inputs and system conditions: POST /patients/{id}/reason on the cloud app, as the browser posts the emergency
//   access form without JavaScript, from the site's own page (Sec-Fetch-Site: same-origin).
// Test data: Patient Lane Unsaved, not on the clinician's panel. Reason "Covering the night shift".
// Steps:
//   1. Post the emergency access reason for the patient off-site.
//   2. Fetch the stylesheet and script the answer links to.
// Expected result: The answer is 503, an HTML page: "Not saved. The clinic's server can't be reached. Copy what you
//   entered below, then go back and try again once the server is back.", the reason "Covering
//   the night shift" in a read-only box, and a "Back" link to the form. The page's only assets are the site's
//   stylesheet and back-link.js, which the cloud app serves. No emergency access is recorded.
// Creator: Tahseen Arnan
public partial class TC_UC2_03_InvalidInput(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_form_post_that_cannot_be_forwarded_gets_a_page_that_explains()
    {
        var patient = await apps.InsertPatientAsync("Lane", "Unsaved");
        var signedIn = await CookieHeaderAsync(apps.OnPrem, await apps.ClinicianAsync());
        using var client = CreateClient(apps.CloudCutOff, handleCookies: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/patients/{patient}/reason")
        {
            Content = Form(("Reason", "Covering the night shift")),
        };
        request.Headers.Add("Cookie", signedIn);
        request.Headers.Add("Sec-Fetch-Site", "same-origin");

        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("Not saved.</span> The clinic's server can't be reached. Copy what you entered below, then go back and try again once the server is back.", html);
        // What was typed, to copy.
        Assert.Contains("<label>Reason <textarea readonly rows=\"4\">\nCovering the night shift</textarea></label>", html);
        // Back to the form, by the browser's history where JavaScript runs, by its address where it doesn't.
        Assert.Contains($"<a href=\"/patients/{patient}/reason\" data-back>Back</a>", html);
        var assets = PageAssets().Matches(html).Select(match => match.Groups[1].Value).ToList();
        // The page's icons, styles, script and mark: all the cloud app's own, each answered with the link cut.
        Assert.Equal(
            [
                "/_content/Ehr.Design/img/favicon.svg",
                "/_content/Ehr.Design/img/favicon-32.png",
                "/_content/Ehr.Design/img/apple-touch-icon.png",
                "/_content/Ehr.Design/css/tokens.css",
                "/_content/Ehr.Design/css/ehr.css",
                "/_content/Ehr.Design/js/back-link.js",
                "/_content/Ehr.Design/img/swiftchart-mark.svg",
                "/_content/Ehr.Design/img/swiftchart-mark-reversed.svg",
            ],
            assets);
        foreach (var asset in assets)
        {
            using var file = await client.GetAsync(asset, Cancel);
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        }

        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.BreakGlass, patientId: patient));
    }

    [GeneratedRegex("(?:href|src)=\"(/_content/[^\"]+)\"")]
    private static partial Regex PageAssets();
}
