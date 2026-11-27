using Ehr.Data.Audit;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RecordViewTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-47
// Purpose / scenario: Off-site, Who viewed my record is read from the cloud copy, while the clinic's server can't be
//   reached too; looking is recorded in the cloud app's audit queue, as a read of the patient's part of the log with no
//   row, and reaches the clinic's log when it is shipped, after which it is listed. The page says a view from the last
//   minute may not be listed yet.
// Type: Functional (exceptional condition: off-site)
// Requirements: proposed FR-57, NFR-03
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a confirmed portal account and a portal session. Dr. Test has the patient on their
//   panel and has opened the chart.
// Inputs and system conditions: GET /portal/views on the cloud app cut off from the clinic's server, then on the
//   connected cloud app, whose queue is then shipped; then on the on-site app.
// Test data: None beyond the accounts.
// Steps:
//   1. On the app that can't reach the clinic's server, open Who viewed my record.
//   2. On the connected cloud app, open it; its shipper runs.
//   3. On the on-site app, open it.
// Expected result: Step 1: Dr. Test's view is listed, with the read-only notice and "a view from the last minute may
//   not be listed yet"; the look is queued as a read of audit_events with no row, and not in the clinic's log. Step 2:
//   the look arrives with source cloud. Step 3: the patient's looks from the cloud are listed as "Who viewed your record".
// Creator: Tahseen Arnan
public class TC_UC6_47_WhoViewedOffSite(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Off_site_the_page_reads_the_cloud_copy_and_its_look_arrives_from_the_cloud()
    {
        var (_, session, patient) = await ViewedPatientAsync(apps, "Offsite");
        await session.Client.GetStringAsync($"/patients/{patient.Patient}", Cancel);
        var looks = await apps.AuditEventsAsync(AuditActions.Read, patient.Account, patient.Patient);

        // Step 1.
        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));
        var cutOff = Words(await Client(apps.CloudCutOff, patient.Jar).GetStringAsync(ViewsPath, Cancel));
        Assert.Contains("Dr. Test, clinician", cutOff);
        Assert.Contains("a view from the last minute may not be listed yet", cutOff);
        Assert.Contains("Read-only. The clinic's server can't be reached", cutOff);
        var (queued, _) = apps.CloudCutOff.Services.GetRequiredService<CloudAuditQueue>().ReadPending(int.MaxValue);
        Assert.Single(queued, e => e.Action == AuditActions.Read && e.ActorId == patient.Account && e.PatientId == patient.Patient
            && e.TableName == "audit_events" && e.RowId is null);
        Assert.Equal(looks, await apps.AuditEventsAsync(AuditActions.Read, patient.Account, patient.Patient));

        // Step 2.
        Assert.Contains("Dr. Test, clinician", Words(await Client(apps.Cloud, patient.Jar).GetStringAsync(ViewsPath, Cancel)));
        await apps.ShipCloudAuditAsync();
        Assert.Equal(["cloud"], await ProxyTests.QueryAsync(apps,
            $"SELECT source FROM ehr.audit_events WHERE patient_id = @patient AND actor_id = '{patient.Account}' AND table_name = 'audit_events'", patient.Patient));

        // Step 3.
        Assert.StartsWith("you | You | Who viewed your record", Lines(await PageAsync(patient.Client))[0], StringComparison.Ordinal);
    }
}
