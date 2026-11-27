using System.Net;
using Ehr.Data.Audit;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OffSiteOutageTests;

namespace Ehr.Web.Tests.TestCases.UC2;

// Test Case ID: TC-UC2-02
// Purpose / scenario: The on-site server comes back after an outage. The read-only notice goes, the reads queued
//   during the outage arrive in the on-site audit log, and saving works again.
// Type: Functional (alternative flow)
// Requirements: FR-32, FR-33, FR-34
// Use case: UC 2, Read records during an on-site outage
// Preconditions: The clinician signed in off-site while the on-site server was up, and the patient is on their panel.
//   The on-site app's database is then down (PrimaryDown on the CloudPrimaryDrill app), and the cloud app's check has
//   found that out.
// Inputs and system conditions: On the cloud app: GET the chart and the quick texts page during the outage; then the
//   on-site database comes back and the cloud app's check runs; then GET the quick texts page and POST a new quick
//   text as htmx sends it.
// Test data: Patient Remi Returning. A quick text with a new random shortcut and the body "Saved once it's back".
// Steps:
//   1. During the outage, open the patient's chart and the quick texts page off-site.
//   2. Bring the on-site database back and let the cloud app's check run.
//   3. Open the quick texts page again and add the quick text.
// Expected result: During the outage the chart shows "Returning, Remi", the quick texts page shows the notice
//   "Read-only. The clinic's server can't be reached" and no form to add one, and the on-site audit log has no read
//   of the patient. After recovery the on-site audit log has the one read, with source "cloud"; the quick texts page
//   no longer shows the notice and offers the form; and the new quick text is saved once on the primary, at
//   version 1.
// Creator: Tahseen Arnan
public class TC_UC2_02_AlternativeFlow(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Once_the_clinics_server_is_back_queued_reads_arrive_and_saves_work_again()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Remi", "Returning");
        var session = await SignInAsync(apps.CloudPrimaryDrill, clinician, via: apps.Cloud);
        // Signed in first: the home lists the panel, and that would be a read too.
        await apps.AssignAsync(clinician, patient);
        var shipper = apps.CloudPrimaryDrill.Services.GetRequiredService<CloudAuditShipper>();
        var shortcut = WebApps.NewShortcut();
        try
        {
            apps.PrimaryDown = true;
            Assert.False(await shipper.ShipAsync(Cancel));
            Assert.Contains("Returning, Remi", await session.Client.GetStringAsync($"/patients/{patient}", Cancel));
            var readOnly = await session.Client.GetStringAsync(QuickTexts, Cancel);
            Assert.Contains("Read-only. The clinic's server can't be reached", Words(readOnly));
            Assert.DoesNotContain("add-quick-text", readOnly);
            Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient));

            apps.PrimaryDown = false;
            Assert.True(await shipper.ShipAsync(Cancel));
            Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient, AuditSources.Cloud));

            var page = await session.Client.GetStringAsync(QuickTexts, Cancel);
            Assert.DoesNotContain("Read-only.", page);
            Assert.Contains("add-quick-text", page);
            using var added = await HtmxPostAsync(session.Client, $"{QuickTexts}?handler=Add", Token(page), ("Shortcut", shortcut), ("Body", "Saved once it's back"));
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            Assert.Equal([(shortcut, "Saved once it's back", 1)], await apps.RowsAsync(shortcut));
        }
        finally
        {
            apps.PrimaryDown = false;
        }
    }
}
