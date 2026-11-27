using Ehr.Data.Audit;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC2;

// Test Case ID: TC-UC2-01
// Purpose / scenario: Off-site during an on-site outage, a clinician already signed in opens a chart. It is read from
//   the cloud replica, the page shows the read-only notice, and the read is queued and then shipped to the on-site
//   audit log.
// Type: Functional (successful flow)
// Requirements: FR-02, FR-32, FR-33, FR-34
// Use case: UC 2, Read records during an on-site outage
// Preconditions: The clinician signed in off-site while the on-site server was up, and the patient is on their panel.
//   The on-site app's database then goes down (PrimaryDown on the CloudPrimaryDrill app), and the cloud app's check
//   has found that out.
// Inputs and system conditions: GET /patients/{id} on the cloud app (ehr_read role, the replica), during the outage;
//   then the on-site database comes back and the cloud app's check runs again.
// Test data: Clinician "Dr. Remote". Patient Hazel Outage, allergic to Penicillin, with a visit yesterday: blood
//   pressure 118/76 mmHg, heart rate 64/min.
// Steps:
//   1. With the on-site database down, open the patient's chart off-site.
//   2. Read the cloud app's audit queue and the on-site audit log.
//   3. Bring the on-site database back and let the cloud app's check run.
//   4. Read the on-site audit log again.
// Expected result: The chart shows "Outage, Hazel", "Penicillin" and "blood pressure 118/76 mmHg, heart rate
//   64/min.", with the notice "Read-only. The clinic's server can't be reached, so nothing can be saved until it's
//   back. The newest changes may not show yet." One read of the patient, by the
//   clinician, on table patients, waits in the cloud app's queue, and the on-site audit log has none. After recovery
//   the on-site audit log has that one read, with source "cloud".
// Creator: Tahseen Arnan
public class TC_UC2_01_SuccessfulFlow(WebApps apps) : IClassFixture<WebApps>
{
    static readonly DateTimeOffset Today = DateTimeOffset.UtcNow;

    [Fact]
    public async Task Off_site_during_an_outage_the_chart_comes_from_the_replica_and_its_one_read_is_queued_then_shipped()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Remote", Ehr.Data.Accounts.StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Hazel", "Outage");
        await apps.InsertAllergyAsync(patient, "Penicillin");
        await apps.InsertVisitAsync(patient, Today.AddDays(-1), systolic: 118, diastolic: 76, heartRate: 64);
        // Signed in first: the home lists the panel, and that would be a read too.
        var session = await SignInAsync(apps.CloudPrimaryDrill, clinician, via: apps.Cloud);
        await apps.AssignAsync(clinician, patient);
        var shipper = apps.CloudPrimaryDrill.Services.GetRequiredService<CloudAuditShipper>();
        apps.PrimaryDown = true;
        try
        {
            Assert.False(await shipper.ShipAsync(Cancel));

            var html = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);

            Assert.Contains("Outage, Hazel", html);
            Assert.Contains("Penicillin", html);
            Assert.Contains("blood pressure 118/76 mmHg, heart rate 64/min.", html);
            Assert.Contains("Read-only. The clinic's server can't be reached, so nothing can be saved until it's back. The newest changes may not show yet.", Words(html));
            var (queued, _) = apps.CloudPrimaryDrill.Services.GetRequiredService<CloudAuditQueue>().ReadPending(int.MaxValue);
            var read = Assert.Single(queued, e => e.PatientId == patient);
            Assert.Equal(AuditActions.Read, read.Action);
            Assert.Equal("patients", read.TableName);
            Assert.Equal(patient, read.RowId);
            Assert.Equal(clinician.Id, read.ActorId);
            Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient));

            apps.PrimaryDown = false;
            Assert.True(await shipper.ShipAsync(Cancel));
            Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient, AuditSources.Cloud));
        }
        finally
        {
            apps.PrimaryDown = false;
        }
    }
}
