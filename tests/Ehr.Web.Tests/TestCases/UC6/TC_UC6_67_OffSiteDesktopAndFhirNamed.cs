using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RecordViewTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-67
// Purpose / scenario: A view off-site, through the FHIR API, or into the desktop app's offline copy is named too: the
//   part a page on the cloud app records goes through its audit queue to the clinic's log, and the page names it.
// Type: Functional (alternative flow: off-site, the API and the desktop app)
// Requirements: proposed FR-57
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a confirmed portal account, signed in. Dr. Offline, a clinician, signed in on the
//   on-site app and on the desktop app, then put the patient on their panel.
// Inputs and system conditions: The patient GETs /portal/results on the cloud app, whose audit queue is then shipped;
//   Dr. Offline GETs /fhir/Patient/{id} and the desktop app POSTs /api/desktop/offline-panel; the patient GETs
//   /portal/views on the on-site app.
// Test data: None beyond the accounts.
// Steps:
//   1. The patient opens their test results off-site, and the cloud app ships its audit queue.
//   2. Dr. Offline reads the patient through the FHIR API, and the desktop app takes its offline copy.
//   3. The patient opens Who viewed my record.
// Expected result: Step 1: the read arrives in the clinic's log from the cloud with the part "results". Step 3: the
//   patient's own line "Your test results"; Dr. Offline's, "Your record, through the clinic's connection for health apps (FHIR)" and
//   "Your chart, saved on a clinician's computer to use offline".
// Creator: Tahseen Arnan
public class TC_UC6_67_OffSiteDesktopAndFhirNamed(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Views_off_site_through_the_api_and_into_the_offline_copy_are_named()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Offline", StaffRoles.Clinician);
        var patient = await ProxyTests.PersonAsync(apps, "Synthetic", "Offsitenamed");
        var session = await SignInAsync(apps.OnPrem, clinician);
        var token = await apps.DesktopSignInAsync(apps.OnPrem, clinician);
        await AssignNowAsync(apps, clinician, patient.Patient);

        // Step 1.
        var since = DateTimeOffset.UtcNow;
        using (var results = await Client(apps.Cloud, patient.Jar).GetAsync("/portal/results", Cancel))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, results.StatusCode);
        }

        await apps.ShipCloudAuditAsync();
        Assert.Equal(["results cloud"], await ProxyTests.QueryAsync(apps,
            $"""
            SELECT part || ' ' || source FROM ehr.audit_events
            WHERE patient_id = @patient AND actor_id = '{patient.Account}' AND action = '{AuditActions.Read}' AND occurred_at >= '{since:O}'
            """, patient.Patient));

        // Step 2.
        Assert.Equal(System.Net.HttpStatusCode.OK, (await session.Client.GetAsync($"/fhir/Patient/{patient.Patient}", Cancel)).StatusCode);
        using (var offline = await DesktopSignInTests.PostAsync(apps.OnPrem, "/api/desktop/offline-panel", token))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, offline.StatusCode);
        }

        // Step 3.
        var html = await PageAsync(patient.Client);
        Assert.Contains("Your test results", PartsOf(html, "you").Select(Uncounted));
        Assert.Equal(["Your chart, saved on a clinician's computer to use offline", "Your record, through the clinic's connection for health apps (FHIR)"], PartsOf(html, "staff"));
    }
}
