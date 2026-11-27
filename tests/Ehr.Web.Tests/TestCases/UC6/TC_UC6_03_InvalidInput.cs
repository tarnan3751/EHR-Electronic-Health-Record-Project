using System.Net;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-03
// Purpose / scenario: Only a signed-in patient reaches results. Someone signed out, and a clinician signed in to the
//   clinical site, both ask for the portal's results page, on the on-site app and on the cloud app; neither gets it,
//   and each is sent to the portal's sign-in page instead.
// Type: Functional (invalid input)
// Requirements: FR-27, FR-29
// Use case: UC 6, View test results in the portal
// Preconditions: The clinic's server is up. The clinician is signed in to the clinical site of the app being tested,
//   with password and authenticator code. Nobody is signed in to the portal.
// Inputs and system conditions: GET /portal/results, without following redirects, on the on-site app (ehr_app role)
//   and on the cloud app (ehr_read role): once with no cookies, once with the clinician's staff cookie.
// Test data: A new clinician account from the test fixture, with an address at example.test.
// Steps:
//   1. On each app, ask for /portal/results with no cookies.
//   2. On each app, sign in as the clinician, and ask for /portal/results with the staff session's cookies.
// Expected result: On both apps, both requests are answered 302 to /portal/sign-in, with ReturnUrl=/portal/results:
//   the portal accepts only the patient cookie, so the staff cookie counts as nobody signed in (FR-27, separate staff
//   and patient sign-ins), and the page is refused to anyone who isn't a patient (FR-29). Neither is answered 200.
// Creator: Simar Rekhi
public class TC_UC6_03_InvalidInput(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Nobody_signed_out_and_no_clinician_reaches_the_results_page()
    {
        var clinician = await apps.ClinicianAsync();
        foreach (var app in new[] { apps.OnPrem, apps.Cloud })
        {
            using var anonymous = await Client(app, new CookieContainer(), followRedirects: false).GetAsync("/portal/results", Cancel);
            AssertSentToPortalSignIn(anonymous);

            var staff = await SignInAsync(app, clinician);
            using var asStaff = await Client(app, staff.Jar, followRedirects: false).GetAsync("/portal/results", Cancel);
            AssertSentToPortalSignIn(asStaff);
        }
    }

    static void AssertSentToPortalSignIn(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = new Uri(Base, response.Headers.Location!);
        Assert.Equal("/portal/sign-in", location.AbsolutePath);
        Assert.Contains("ReturnUrl=%2Fportal%2Fresults", location.Query, StringComparison.OrdinalIgnoreCase);
    }
}
