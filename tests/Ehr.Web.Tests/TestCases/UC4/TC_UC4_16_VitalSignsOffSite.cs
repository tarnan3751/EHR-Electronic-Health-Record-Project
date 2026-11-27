using System.Net;
using Ehr.Data.Audit;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VitalTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-16
// Purpose / scenario: Off-site, the vital signs are read from the cloud copy and a new set is forwarded to the clinic's
//   server; while that server can't be reached the flowsheet and trends are still read, and recording is not offered.
// Type: Functional (exceptional condition)
// Requirements: FR-08, FR-14, FR-15, proposed FR-48
// Use case: UC 4, View patient chart
// Preconditions: The clinician is signed in with the patient on their panel. The off-site app can reach the clinic's
//   server; a second off-site app can't.
// Inputs and system conditions: GET and POST /patients/{id}/vitals on the off-site app, then on the one cut off.
// Test data: Heart rate 72, then 64.
// Steps:
//   1. Off-site, record the two heart rates.
//   2. On the app that is cut off, open the vital signs, and post a third set to it.
// Expected result: Step 1: each post is forwarded and saved once; the read is in the audit log from the cloud. Step
//   2: the flowsheet shows 64 and 72 and the heart rate's trend, and says "Vital signs can't be recorded until the
//   clinic's server is back." with no form; the post is answered 503 and nothing is saved.
// Creator: Tahseen Arnan
public class TC_UC4_16_VitalSignsOffSite(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Off_site_vital_signs_are_forwarded_and_with_the_server_down_they_are_read_only()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        var cloud = Client(apps.Cloud, session.Jar);

        Assert.Contains("No vital signs.", await cloud.GetStringAsync(Path(patient), Cancel));
        Assert.True(await apps.Cloud.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));
        Assert.True(await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient, AuditSources.Cloud) >= 1);
        using var first = await RecordAsync(apps, cloud, patient, 60, (HeartRate, "72"));
        using var second = await RecordAsync(apps, cloud, patient, 5, (HeartRate, "64"));
        Assert.Contains($"POST {Path(patient)}", apps.Forwarded);
        Assert.Equal([$"{HeartRate} 72", $"{HeartRate} 64"], await RecordedAsync(apps, patient));
        var token = Token(await session.Client.GetStringAsync(Path(patient), Cancel));

        var cutOff = Client(apps.CloudCutOff, session.Jar);
        var readOnly = await cutOff.GetStringAsync(Path(patient), Cancel);
        Assert.Contains($"data-trend=\"{HeartRate}\"", readOnly);
        Assert.Contains("Latest 64 per minute, down from 72.", Words(readOnly));
        Assert.Contains("Vital signs can't be recorded until the clinic's server is back.", readOnly);
        Assert.DoesNotContain("data-record-vitals", readOnly);
        using var notSaved = await cutOff.PostAsync(Path(patient), Form(("__RequestVerificationToken", token), ($"Values[{HeartRate}]", "90"), ("TakenAt", "2026-11-01T08:00")), Cancel);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notSaved.StatusCode);

        Assert.Equal(2, (await RecordedAsync(apps, patient)).Count);
    }
}
