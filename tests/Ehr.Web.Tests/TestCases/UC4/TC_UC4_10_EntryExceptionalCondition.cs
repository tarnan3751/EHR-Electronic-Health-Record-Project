using System.Net;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.ChartListTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-10
// Purpose / scenario: Two things that stop a change to the chart's lists. A clinician without the patient on their
//   panel, under emergency access, is offered no "Add" or "Change" and is sent back to the chart before anything is
//   read or saved. And off-site, with the clinic's server unreachable, the chart offers neither and says why, and a
//   post that was already on screen isn't saved and shows what was typed again.
// Type: Functional (exceptional condition)
// Requirements: FR-02, FR-33, FR-34, proposed FR-38
// Use case: UC 4, View a patient's chart (editing the chart's lists)
// Preconditions: The patient is on one clinician's panel and has one diagnosis. A second clinician, "Dr. Covering
//   Lists", has emergency access to the patient. For the second part, the cloud app's link to the on-site server is down.
// Inputs and system conditions: GET /patients/{id}, GET and POST /patients/{id}/diagnoses[/{entry}] on the on-site
//   app as the second clinician; then on the cloud app whose link is down, as the first.
// Test data: Patient Synthetic Listed. Diagnosis "Essential (primary) hypertension". Typed off-site: "Unspecified
//   asthma, uncomplicated".
// Steps:
//   1. As the second clinician, open the chart with a reason; request the add and change forms; post each.
//   2. Off-site with the link down, open the chart as the first clinician.
//   3. Post a new diagnosis with the form token read before the link went down.
// Expected result: In step 1 the chart lists the diagnosis with no "Add" or "Change"; each form request is answered
//   with a redirect to the chart, and each post with 409 and "Not saved. This isn't one of your patients.", the
//   typed name to copy and nothing about the patient; no new read is recorded and nothing changes. In step 2 the chart lists the
//   diagnosis, offers no "Add" or "Change", and says "Changes to diagnoses need the clinic's server." In step 3 the
//   post is answered 503 with "Not saved. The clinic's server can't be reached." and the typed name
//   shown again. The patient still has exactly one diagnosis.
// Creator: Tahseen Arnan (expansion step 2), UC 4 owner Diego Valenzuela
public class TC_UC4_10_EntryExceptionalCondition(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Off_the_panel_or_off_site_with_the_server_down_the_lists_are_not_changed()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        using var added = await SaveAsync(session.Client, patient, "diagnoses", null, ("Name", "Essential (primary) hypertension"), ("Status", "active"));
        var entry = await IdAsync(apps, "conditions", patient);
        var token = Token(await session.Client.GetStringAsync(EntryPath(patient, "diagnoses"), Cancel));

        var covering = await apps.CreateStaffAsync("Dr. Covering Lists", StaffRoles.Clinician);
        var other = await SignInAsync(apps.OnPrem, covering);
        using var opened = await PostFormAsync(other.Client, $"/patients/{patient}/reason", $"/patients/{patient}/reason", ("Reason", "Covering the night shift"));
        var chart = await ReadAsync(opened);
        Assert.Contains("<strong>Essential (primary) hypertension</strong>", chart);
        Assert.DoesNotContain("data-add-entry", chart);
        Assert.DoesNotContain("data-change-entry", chart);
        var reads = await apps.AuditEventsAsync(AuditActions.Read, covering.Id, patient);
        var noRedirects = Client(apps.OnPrem, other.Jar, followRedirects: false);
        foreach (var path in new[] { EntryPath(patient, "diagnoses"), EntryPath(patient, "diagnoses", entry) })
        {
            using var form = await noRedirects.GetAsync(path, Cancel);
            Assert.Equal($"/patients/{patient}", new Uri(Base, form.Headers.Location!).AbsolutePath);
            using var post = await noRedirects.PostAsync(path,
                Form(("__RequestVerificationToken", Token(chart)), ("Name", "Unspecified asthma, uncomplicated"), ("Status", "resolved")), Cancel);
            Assert.Equal(HttpStatusCode.Conflict, post.StatusCode);
            var offPanel = await post.Content.ReadAsStringAsync(Cancel);
            Assert.Contains("Not saved. This isn't one of your patients.", Words(offPanel));
            Assert.Contains("Unspecified asthma, uncomplicated</textarea>", offPanel);
            Assert.DoesNotContain("Listed, Synthetic", offPanel);
        }

        Assert.Equal(reads, await apps.AuditEventsAsync(AuditActions.Read, covering.Id, patient));

        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));
        var cutOff = Client(apps.CloudCutOff, session.Jar);
        var readOnly = await cutOff.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.Contains("<strong>Essential (primary) hypertension</strong>", readOnly);
        Assert.DoesNotContain("data-add-entry", readOnly);
        Assert.DoesNotContain("data-change-entry", readOnly);
        Assert.Contains("Changes to diagnoses need the clinic's server.", readOnly);

        using var request = new HttpRequestMessage(HttpMethod.Post, EntryPath(patient, "diagnoses"));
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Content = Form(("__RequestVerificationToken", token), ("Name", "Unspecified asthma, uncomplicated"), ("Status", "active"));
        using var posted = await cutOff.SendAsync(request, Cancel);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, posted.StatusCode);
        var notSaved = await posted.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("Not saved.</span> The clinic's server can't be reached.", notSaved);
        Assert.Contains("Unspecified asthma, uncomplicated", notSaved);

        Assert.Equal(["Essential (primary) hypertension active"], await RowsAsync(apps, "conditions", patient, "concat_ws(' ', display, clinical_status)"));
    }
}
