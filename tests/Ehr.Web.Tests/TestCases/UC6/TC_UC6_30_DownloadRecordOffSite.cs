using Ehr.Data.Audit;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RecordDownload;
using R4 = Hl7.Fhir.Model;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-30
// Purpose / scenario: Off-site, the patient downloads and prints their record from the cloud copy, with the clinic's server
//   down as with it up: the cloud app answers both itself, forwards nothing, and records each in its audit queue before
//   the answer is sent; the events reach the clinic's audit log when shipped, with source cloud.
// Type: Functional (exceptional condition)
// Requirements: FR-15, proposed FR-56
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: As TC-UC6-24. Two off-site apps: one that can't reach the clinic's server, one that can.
// Inputs and system conditions: GET /portal/record, /portal/record?handler=File and /portal/record/print on the app that
//   can't reach the clinic's server; GET /portal/record?handler=File&sensitive=true on the one that can, and its shipper
//   run.
// Test data: FhirRequests.ChartAsync's record.
// Steps:
//   1. On the app cut off from the clinic's server, open Download my record, download the file and open the printable
//      page.
//   2. On the connected app, download the file with sensitive items; its shipper runs.
// Expected result: Step 1: the page and the printable page say "Read-only:"; the file is the record, valid; nothing is
//   forwarded; the download and the view are in the cut-off app's queue, under the patient's account, and not in the
//   clinic's log. Step 2: the download of sensitive items arrives in the clinic's log with source cloud.
// Creator: Tahseen Arnan
public class TC_UC6_30_DownloadRecordOffSite(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Off_site_the_copy_comes_from_the_cloud_copy_and_its_record_is_queued_first()
    {
        var (_, patient) = await PortalChartAsync(apps, "Dr. Away");

        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));
        var forwarded = apps.Forwarded.Count;
        var cutOff = Client(apps.CloudCutOff, patient.Jar);
        Assert.Contains("Read-only. The clinic's server can't be reached", Words(await cutOff.GetStringAsync(PagePath, Cancel)));
        var file = Bundle(await DownloadAsync(cutOff));
        Assert.Equal(["lisinopril 10 MG Oral Tablet"], Entries<R4.MedicationStatement>(file).Select(m => (m.Medication as R4.CodeableConcept)?.Text));
        var page = Words(await PrintAsync(cutOff));
        Assert.Contains("Read-only. The clinic's server can't be reached", page);
        Assert.Contains("Health record of Synthetic Fhirchart", page);
        Assert.Equal(forwarded, apps.Forwarded.Count);

        var (queued, _) = apps.CloudCutOff.Services.GetRequiredService<CloudAuditQueue>().ReadPending(int.MaxValue);
        Assert.Contains(queued, e => e.Action == AuditActions.Download && e.PatientId == patient.Patient && e.ActorId == patient.Account && e.RowId == patient.Patient);
        Assert.Contains(queued, e => e.Action == AuditActions.Read && e.PatientId == patient.Patient && e.ActorId == patient.Account && e.TableName == "patients");
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.Download, patient.Account, patient.Patient));

        var connected = Client(apps.Cloud, patient.Jar);
        Bundle(await DownloadAsync(connected, sensitive: true));
        await apps.ShipCloudAuditAsync();
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.DownloadSensitive, patient.Account, patient.Patient, AuditSources.Cloud));
    }
}
