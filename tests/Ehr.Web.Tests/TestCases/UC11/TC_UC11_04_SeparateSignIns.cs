using System.Net;
using Ehr.Data.Accounts;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC11;

// Test Case ID: TC-UC11-04
// Purpose / scenario: Staff and patient sign-ins are kept apart. Staff credentials don't sign in at the portal, patient
//   credentials don't sign in at the staff page, and a signed-in staff session doesn't open the portal.
// Type: Functional (exceptional condition: credentials used on the wrong site)
// Requirements: FR-27, FR-29
// Use case: UC 11, Sign in with multi-factor authentication
// Preconditions: A clinician account (set up, with an authenticator) and a confirmed portal account for a synthetic
//   patient exist. Both browsers are signed out.
// Inputs and system conditions: POST the clinician's email and password to /portal/sign-in on the cloud app; POST the
//   patient's to /sign-in on the on-prem app; sign the clinician in on the cloud app and GET /portal with that
//   session. The clinic's server is up.
// Test data: Clinician "Dr. Separate", with a fresh @example.test address and the fixture's staff password. Synthetic
//   patient "Jordan Portal", with a confirmed portal account at a fresh @example.test address.
// Steps:
//   1. Enter the clinician's email and password on the portal's sign-in page.
//   2. Enter the patient's email and password on the staff sign-in page.
//   3. Sign in as the clinician, then open /portal in the same browser.
// Expected result: Steps 1 and 2 each stay on their sign-in page with "Wrong email or password." and sign nobody in.
//   Step 3 is redirected to /portal/sign-in: the staff session isn't accepted by the portal.
// Creator: Luke Garcia
public class TC_UC11_04_SeparateSignIns(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Staff_and_patient_sign_ins_are_kept_apart()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Separate", StaffRoles.Clinician);
        var (patientEmail, patientPassword) = await apps.CreatePatientAccountAsync(await apps.InsertPatientAsync("Jordan", "Portal"));

        using var staffOnPortal = await PostFormAsync(Client(apps.Cloud, new CookieContainer()), "/portal/sign-in", "/portal/sign-in",
            ("Input.Email", clinician.Email), ("Input.Password", clinician.Password));
        Assert.Equal("/portal/sign-in", PathOf(staffOnPortal));
        Assert.Contains("Wrong email or password.", await ReadAsync(staffOnPortal));

        using var patientOnStaff = await PostFormAsync(Client(apps.OnPrem, new CookieContainer()), "/sign-in", "/sign-in",
            ("Input.Email", patientEmail), ("Input.Password", patientPassword));
        Assert.Equal("/sign-in", PathOf(patientOnStaff));
        Assert.Contains("Wrong email or password.", await ReadAsync(patientOnStaff));

        var session = await SignInAsync(apps.Cloud, clinician);
        using var portal = await Client(apps.Cloud, session.Jar, followRedirects: false).GetAsync("/portal", Cancel);
        Assert.Equal(HttpStatusCode.Redirect, portal.StatusCode);
        Assert.Equal("/portal/sign-in", new Uri(Base, portal.Headers.Location!).AbsolutePath);
    }
}
