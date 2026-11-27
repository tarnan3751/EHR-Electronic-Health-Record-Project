using System.Net;
using Ehr.Data.Audit;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RefillRequestTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-55
// Purpose / scenario: Off-site, the page is read from the cloud copy and a request is a save, forwarded to the clinic's
//   server. While that can't be reached, the page stays readable, offers no form and says why, and a request sent from
//   a page opened before the outage gets the "Not saved" page with the note typed, to copy; nothing is saved.
// Type: Functional (exceptional condition: off-site and an outage)
// Requirements: NFR-08, proposed FR-58
// Use case: UC 6, View my results and appointments in the portal (with UC 2, Read records during an on-site outage)
// Preconditions: The patient has a portal account and is signed in; Dr. Test has the patient on their panel.
// Inputs and system conditions: GET and POST /portal/refills on the cloud app, then on the cloud app cut off from the
//   clinic's server.
// Test data: lisinopril 10 MG Oral Tablet and amlodipine 5 MG Oral Tablet; the note "Synthetic <note> off-site.".
// Steps:
//   1. On the cloud app, open the page and ask for lisinopril.
//   2. Open the page on the clinic's server, then, cut off, send a request for amlodipine with the note.
//   3. Cut off, open the page.
// Expected result: Step 1: the post is forwarded and the request saved. Step 2: 503, the "Not saved" page with the note
//   to copy; no request for amlodipine. Step 3: the requests are listed, with "Read-only: requests can't be sent until
//   the clinic's server is back." and no form; the page's read waits in the cloud app's queue.
// Creator: Tahseen Arnan
public class TC_UC6_55_RefillOffSite(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Off_site_a_request_is_forwarded_and_during_an_outage_the_page_is_read_only()
    {
        var (_, _, patient) = await PortalPatientAsync(apps, "Remote");
        await MedicationAsync(apps, patient.Patient);
        await MedicationAsync(apps, patient.Patient, "amlodipine 5 MG Oral Tablet", "197361");

        var cloud = Client(apps.Cloud, patient.Jar);
        using (var sent = await AskAsync(cloud, Lisinopril))
        {
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        }

        Assert.Contains("POST /portal/refills", apps.Forwarded);
        Assert.Equal([$"requested|{Lisinopril}|-|{patient.Account}|-|-|-"], await RequestsAsync(apps, patient.Patient));

        var html = await patient.Client.GetStringAsync(PortalPath, Cancel);
        using var request = new HttpRequestMessage(HttpMethod.Post, PortalPath)
        {
            Content = Form(("Medication", ReferenceOf(html, "amlodipine 5 MG Oral Tablet")), ("Note", "Synthetic <note> off-site."), ("__RequestVerificationToken", Token(html))),
        };
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        var cutOff = Client(apps.CloudCutOff, patient.Jar);
        using (var refused = await cutOff.SendAsync(request, Cancel))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            var notSaved = await refused.Content.ReadAsStringAsync(Cancel);
            Assert.Contains("Not saved.</span> The clinic's server can't be reached.", notSaved);
            Assert.Contains("Synthetic &lt;note&gt; off-site.", notSaved);
        }

        Assert.Single(await RequestsAsync(apps, patient.Patient));

        var page = await cutOff.GetStringAsync(PortalPath, Cancel);
        var words = Words(page);
        Assert.Contains("Read-only: requests can't be sent until the clinic's server is back.", words);
        Assert.Contains(Lisinopril, words);
        Assert.Contains("Waiting", words);
        Assert.DoesNotContain("name=\"Medication\"", page);
        // Read from the cloud copy: the read waits in the cloud app's queue, which can't ship it while cut off.
        var (queued, _) = apps.CloudCutOff.Services.GetRequiredService<CloudAuditQueue>().ReadPending(int.MaxValue);
        Assert.Contains(queued, e => e.Action == AuditActions.Read && e.ActorId == patient.Account && e.PatientId == patient.Patient && e.TableName == "patients");
    }
}
