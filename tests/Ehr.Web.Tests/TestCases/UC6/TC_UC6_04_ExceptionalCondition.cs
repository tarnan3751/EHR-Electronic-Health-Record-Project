using Ehr.Data.Audit;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.PortalResultsTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-04
// Purpose / scenario: The on-site primary server is down (UC 2's outage course). A patient off-site opens their test
//   results: they are read from the cloud copy, the page is marked read-only, and the read is recorded in the cloud
//   app's queue for the clinic's server.
// Type: Functional (exceptional condition)
// Requirements: FR-16, FR-32, FR-34
// Use case: UC 6, View test results in the portal
// Preconditions: The patient has a confirmed portal account and signed in through the cloud app while it could reach
//   the clinic's server. The cloud app's link to the clinic's server is then down, and its audit queue can't ship.
// Inputs and system conditions: GET /portal/results on the cut-off cloud app (ehr_read role), with the session cookie
//   from the earlier sign-in.
// Test data: Patient Avery Offsite with Hemoglobin (LOINC 718-7) 13.2 g/dL, range 12.0 to 15.5 g/dL, sampled and
//   released Nov 2, 2026 15:00 UTC.
// Steps:
//   1. Sign in to the portal as Avery Offsite through the connected cloud app.
//   2. Try to ship the cut-off cloud app's audit queue (it fails: the clinic's server can't be reached).
//   3. Open /portal/results on the cut-off cloud app with the session cookie.
// Expected result: The page shows "13.2 g/dL" and the banner "Read-only. The clinic's server can't be reached, so
//   nothing can be saved until it's back. The newest changes may not show yet.", and the cut-off cloud app's audit
//   queue holds a read of the patient.
// Creator: Simar Rekhi
public class TC_UC6_04_ExceptionalCondition(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Off_site_with_the_clinic_server_down_results_are_read_only_and_the_read_is_queued()
    {
        var patient = await apps.InsertPatientAsync("Avery", "Offsite");
        await apps.InsertResultAsync(patient, "Hemoglobin", "718-7", 13.2m, "g/dL", 12.0m, 15.5m, Nov2, Nov2);
        // Signed in through the connected cloud app; the cookie works on the cut-off one, as it would after an outage.
        var (_, jar) = await SignInPatientAsync(apps, apps.Cloud, patient);
        var cookie = jar.GetCookieHeader(Base);
        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));

        using var client = CreateClient(apps.CloudCutOff, handleCookies: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/portal/results");
        request.Headers.Add("Cookie", cookie);
        using var response = await client.SendAsync(request, Cancel);

        var html = await ReadAsync(response);
        Assert.Contains("13.2 g/dL", html);
        Assert.Contains("Read-only. The clinic's server can't be reached", Words(html));
        var (queued, _) = apps.CloudCutOff.Services.GetRequiredService<CloudAuditQueue>().ReadPending(int.MaxValue);
        Assert.Contains(queued, e => e.Action == AuditActions.Read && e.PatientId == patient);
    }
}
