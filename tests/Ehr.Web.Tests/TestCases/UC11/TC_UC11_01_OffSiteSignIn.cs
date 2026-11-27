using System.Net;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Testing;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC11;

// Test Case ID: TC-UC11-01
// Purpose / scenario: A clinician outside the clinic signs in with their password and the code from their
//   authenticator app. The password alone only reaches the code page; the code signs them in, to a clinician's pages
//   only, and the sign-in is recorded.
// Type: Functional (successful flow)
// Requirements: FR-28, FR-29
// Use case: UC 11, Sign in with multi-factor authentication
// Preconditions: A clinician account exists, is active, and has finished setup with an authenticator app. The
//   browser is signed out.
// Inputs and system conditions: GET /sign-in, POST the email and password, then POST the 6-digit code from
//   /sign-in/code, on the cloud app (the off-site address); then GET /admin/staff with the session. The clinic's server
//   is up.
// Test data: A new clinician, "Dr. Off Site", with a fresh @example.test address, the fixture's staff password and a
//   code computed from the account's authenticator key (Totp.Code), as the phone would show it.
// Steps:
//   1. Open the sign-in page and enter the email and password.
//   2. On the code page, enter the current authenticator code.
//   3. Open the staff administration page.
//   4. Read the audit log for the account's sign-ins.
// Expected result: Step 1 lands on /sign-in/code, with no sign-in recorded yet. Step 2 signs the clinician in and shows
//   "My patients". Step 3 is refused with a redirect to /denied, since the account is a clinician, not an admin. Step 4
//   finds exactly one sign_in event for the account.
// Creator: Luke Garcia
public class TC_UC11_01_OffSiteSignIn(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Off_site_the_password_reaches_the_code_page_and_the_code_signs_the_clinician_in()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Off Site", StaffRoles.Clinician);
        var jar = new CookieContainer();
        var client = Client(apps.Cloud, jar);

        using var codePage = await PostFormAsync(client, "/sign-in", "/sign-in", ("Input.Email", clinician.Email), ("Input.Password", clinician.Password));
        Assert.Equal("/sign-in/code", PathOf(codePage));
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.SignIn, actorId: clinician.Id));

        using var home = await PostFormAsync(client, "/sign-in/code", codePage, ("Code", Totp.Code(clinician.AuthenticatorKey)));
        Assert.Contains("My patients", await ReadAsync(home));

        using var staff = await Client(apps.Cloud, jar, followRedirects: false).GetAsync("/admin/staff", Cancel);
        Assert.Equal(HttpStatusCode.Redirect, staff.StatusCode);
        Assert.Equal("/denied", new Uri(Base, staff.Headers.Location!).AbsolutePath);

        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.SignIn, actorId: clinician.Id));
    }
}
