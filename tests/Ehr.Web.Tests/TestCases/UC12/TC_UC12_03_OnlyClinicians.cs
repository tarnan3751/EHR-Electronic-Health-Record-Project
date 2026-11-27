using System.Net;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC12;

// Test Case ID: TC-UC12-03
// Purpose / scenario: Only a clinician can use emergency access. An admin, who manages accounts and reviews the audit
//   log but doesn't treat patients, is refused the patient pages and the reason form even by typing their addresses,
//   and someone signed out is sent to sign in. Nothing is read or granted for either.
// Type: Functional (alternative flow)
// Requirements: FR-29, with FR-06
// Use case: UC 12, Open a chart through emergency access
// Preconditions: An admin who has finished setup, and a patient off every panel.
// Inputs and system conditions: On-site (the on-prem app). As the admin: GET /patients, /patients/{id} and
//   /patients/{id}/reason, and POST a reason to /patients/{id}/reason with a form token from the admin's own staff page.
//   Signed out: GET /patients/{id}/reason.
// Test data: Admin "Admin No Charts", patient "Remy Guarded". Reason: "Checking a chart as an admin."
// Steps:
//   1. As the admin, open each of the three addresses.
//   2. As the admin, post the reason.
//   3. Signed out, open the reason page.
// Expected result: Steps 1 and 2 each end at /denied, with no chart and no reason form. Step 3 is sent to /sign-in.
//   The audit log has no read or break_glass event from the admin, and ehr.break_glass_accesses has no row for the
//   patient.
// Creator: Luke Garcia
public class TC_UC12_03_OnlyClinicians(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task An_admin_is_refused_the_patient_pages_and_emergency_access_and_a_visitor_is_sent_to_sign_in()
    {
        var admin = await apps.CreateStaffAsync("Admin No Charts", StaffRoles.Admin);
        var patient = await apps.InsertPatientAsync("Remy", "Guarded");
        var session = await SignInAsync(apps.OnPrem, admin);
        var direct = Client(apps.OnPrem, session.Jar, followRedirects: false);

        // 1. Each address, typed in.
        foreach (var path in new[] { "/patients", $"/patients/{patient}", $"/patients/{patient}/reason" })
        {
            using var response = await direct.GetAsync(path, Cancel);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/denied", new Uri(Base, response.Headers.Location!).AbsolutePath);
        }

        // 2. The form posted anyway, with a valid token from a page the admin may see.
        using (var posted = await PostFormAsync(session.Client, $"/patients/{patient}/reason", "/admin/staff", ("Reason", "Checking a chart as an admin.")))
        {
            Assert.Equal("/denied", PathOf(posted));
            var html = await posted.Content.ReadAsStringAsync(Cancel);
            Assert.DoesNotContain("Guarded", html);
            Assert.DoesNotContain("<textarea", html);
        }

        // 3. Signed out.
        using (var visitor = await Client(apps.OnPrem, new CookieContainer(), followRedirects: false).GetAsync($"/patients/{patient}/reason", Cancel))
        {
            Assert.Equal(HttpStatusCode.Redirect, visitor.StatusCode);
            Assert.Equal("/sign-in", new Uri(Base, visitor.Headers.Location!).AbsolutePath);
        }

        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.Read, admin.Id, patient));
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.BreakGlass, admin.Id));
        Assert.Equal(0, await GrantsAsync(patient));
    }

    async Task<long> GrantsAsync(Guid patientId)
    {
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM ehr.break_glass_accesses WHERE patient_id = @patient", db);
        command.Parameters.AddWithValue("patient", patientId);
        return (long)(await command.ExecuteScalarAsync(Cancel))!;
    }
}
