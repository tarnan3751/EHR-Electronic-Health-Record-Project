using Ehr.Data.Audit;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-19
// Purpose / scenario: The patient's own sensitive visit summaries are left out of their page until they ask, as
//   sensitive items are everywhere (docs/brand): the page says how many and offers a link; asking is a read, recorded as
//   a view of sensitive items, which the cloud app answers itself when the clinic's server can't be reached.
// Type: Functional (alternative flow: sensitive summaries)
// Requirements: FR-39, proposed FR-55
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a portal account. Dr. Test, with the patient on their panel, has released a summary
//   of an ordinary note and one, ticked "Sensitive", of another.
// Inputs and system conditions: GET /portal/summaries, then the link it offers, on the on-site app; the same link on
//   the cloud app whose link to the clinic's server is down.
// Test data: The sensitive summary's What we talked about "We talked about something private.".
// Steps:
//   1. Open Visit summaries.
//   2. Follow "Show sensitive summaries".
//   3. Off-site, with the clinic's server down, follow the same link.
// Expected result: Step 1: the ordinary summary, and "Sensitive items hidden: 1 visit summary is not shown. Show
//   sensitive summaries", a link to /portal/summaries?sensitive=true; nothing of the sensitive one. Step 2: both, the
//   sensitive one tagged "Sensitive", and "Sensitive items shown: your view of them is recorded." with "Hide sensitive
//   summaries"; one view of sensitive items recorded in place of the plain read, and a read of each summary shown.
//   Step 3: the same page, read-only, not forwarded, its view queued as a view of sensitive items.
// Creator: Tahseen Arnan
public class TC_UC6_19_SensitiveSummariesAsked(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_patients_sensitive_summaries_wait_until_asked_for_and_asking_is_a_recorded_read()
    {
        var (_, session, patient) = await PortalPatientAsync(apps, "Private");
        var ordinary = await SignedNoteAsync(session.Client, patient.Patient);
        var sensitive = await SignedNoteAsync(session.Client, patient.Patient);
        using (var released = await ReleaseAsync(session.Client, patient.Patient, ordinary))
        {
            Assert.Contains("Released:", Words(await ReadAsync(released)));
        }

        using (var released = await ReleaseAsync(session.Client, patient.Patient, sensitive, "We talked about something private.", sensitive: true))
        {
            Assert.Contains("Released:", Words(await ReadAsync(released)));
        }

        var page = await PortalAsync(patient.Client);
        Assert.Contains(TalkedAbout, page);
        Assert.DoesNotContain("something private", page);
        Assert.Contains("Sensitive items hidden: 1 visit summary is not shown. Show sensitive summaries", Words(page));
        Assert.Contains("<a href=\"/portal/summaries?sensitive=true\">Show sensitive summaries</a>", page);

        var sensitiveViews = await apps.AuditEventsAsync(AuditActions.ReadSensitive, patient.Account, patient.Patient);
        var reads = await apps.AuditEventsAsync(AuditActions.Read, patient.Account, patient.Patient);
        var shown = await PortalAsync(patient.Client, sensitive: true);
        Assert.Contains("We talked about something private.", shown);
        Assert.Contains(">Sensitive</span>", shown);
        Assert.Contains("Sensitive items shown: your view of them is recorded. Hide sensitive summaries", Words(shown));
        Assert.Equal(sensitiveViews + 1, await apps.AuditEventsAsync(AuditActions.ReadSensitive, patient.Account, patient.Patient));
        // No plain read of the patient beside it: a read of each of the two summaries shown.
        Assert.Equal(reads + 2, await apps.AuditEventsAsync(AuditActions.Read, patient.Account, patient.Patient));

        // Off-site, with the clinic's server down: the cloud app answers the link itself.
        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));
        var forwarded = apps.Forwarded.Count;
        var offSite = await Client(apps.CloudCutOff, patient.Jar).GetStringAsync("/portal/summaries?sensitive=true", Cancel);
        Assert.Contains("We talked about something private.", offSite);
        Assert.Contains("Read-only: the clinic's server can't be reached", Words(offSite));
        Assert.Equal(forwarded, apps.Forwarded.Count);
        var (queued, _) = apps.CloudCutOff.Services.GetRequiredService<CloudAuditQueue>().ReadPending(int.MaxValue);
        Assert.Contains(queued, e => e.Action == AuditActions.ReadSensitive && e.PatientId == patient.Patient && e.ActorId == patient.Account);
    }
}
