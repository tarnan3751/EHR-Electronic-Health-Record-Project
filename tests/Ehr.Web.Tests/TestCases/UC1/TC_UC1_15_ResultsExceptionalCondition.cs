using System.Net;
using Ehr.Data.Audit;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.ResultTests;

namespace Ehr.Web.Tests.TestCases.UC1;

// Test Case ID: TC-UC1-15
// Purpose / scenario: Off-site, results are read from the cloud copy, which is recorded through the cloud app's audit
//   queue, and filing and acknowledging are forwarded to the clinic's server. With the clinic's server unreachable the
//   results stay readable, the form and the button are replaced by sentences saying why, and a post already on screen
//   saves nothing.
// Type: Functional (exceptional condition)
// Requirements: FR-05, FR-33, FR-34, proposed FR-45
// Use case: UC 1, Place an order
// Preconditions: The clinician is signed in with the patient on their panel and has signed a complete blood count.
//   For the second part, the cloud app's link to the on-site server is down.
// Inputs and system conditions: GET and POST /patients/{id}/orders/{order}/results on the cloud app, then on the
//   cloud app whose link is down.
// Test data: Hemoglobin 13.5; later platelets 300.
// Steps:
//   1. Off-site, open the order's results and file hemoglobin 13.5.
//   2. With the link down, open the results; post platelets 300 and an acknowledgment from the page that was open.
//   3. With the link back, acknowledge off-site.
// Expected result: Step 1: the read arrives on the clinic's server with source "cloud"; the post is forwarded and the
//   result filed. Step 2: the page shows the result and says "Results can't be entered until the clinic's server is
//   back." and "Not acknowledged. Results can't be acknowledged until the clinic's server is back.", with no form or
//   button; both posts are answered 503 and nothing is saved. Step 3: forwarded, and the acknowledgment is recorded.
// Creator: Tahseen Arnan
public class TC_UC1_15_ResultsExceptionalCondition(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Off_site_results_are_forwarded_and_with_the_server_down_they_are_read_only()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        var order = await SignedAsync(session.Client, patient, "lab", "Complete blood count (CBC)", Cbc);
        var path = Path(patient, order);
        var cloud = Client(apps.Cloud, session.Jar);

        Assert.Contains("No results yet.", await cloud.GetStringAsync(path, Cancel));
        Assert.True(await apps.Cloud.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));
        Assert.True(await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient, AuditSources.Cloud) >= 1);
        using var filed = await FileAsync(cloud, patient, order, (Hemoglobin, "13.5"));
        Assert.Contains($"POST {path}", apps.Forwarded);
        Assert.Equal([$"{Hemoglobin} 13.5 normal"], await FiledAsync(apps, patient));
        var token = Token(await session.Client.GetStringAsync(path, Cancel));

        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));
        var cutOff = Client(apps.CloudCutOff, session.Jar);
        var readOnly = await cutOff.GetStringAsync(path, Cancel);
        Assert.Contains("13.5 g/dL", readOnly);
        Assert.Contains("Results can't be entered until the clinic's server is back.", readOnly);
        Assert.Contains("Not acknowledged. Results can't be acknowledged until the clinic's server is back.", readOnly);
        Assert.DoesNotContain("data-file-results", readOnly);
        Assert.DoesNotContain("data-acknowledge", readOnly);
        foreach (var target in new[] { path, path + "?handler=Acknowledge" })
        {
            using var notSaved = await cutOff.PostAsync(target, Form(("__RequestVerificationToken", token), ($"Values[{Platelets}]", "300"), ("CollectedAt", "2026-11-01T08:00")), Cancel);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, notSaved.StatusCode);
        }

        Assert.Single(await FiledAsync(apps, patient));
        Assert.Empty(await MessageTests.RowsAsync(apps, patient, "id::text", "result_acknowledgments", "id"));

        using var acknowledged = await AcknowledgeAsync(cloud, patient, order);
        Assert.Contains($"POST {path}", apps.Forwarded);
        Assert.Equal([clinician.Id.ToString()], await MessageTests.RowsAsync(apps, patient, "acknowledged_by::text", "result_acknowledgments", "id"));
    }
}
