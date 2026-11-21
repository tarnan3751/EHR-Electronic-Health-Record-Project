using Ehr.Data.Audit;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-20
// Purpose / scenario: Off-site, the patient's visit summaries and the placeholders of withdrawn ones are read from the
//   cloud copy; the reads go to the cloud app's audit queue and reach the clinic's audit log when shipped, with source
//   cloud. A view shipped before a withdrawal counts as viewed, and the patient is emailed; a view still queued at the
//   moment of withdrawal is missed (the known limit: the shipper runs every couple of seconds).
// Type: Functional (exceptional condition)
// Requirements: FR-15, proposed FR-55
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a confirmed portal account, signed in. Dr. Test, with the patient on their panel, has
//   released a summary of one note, withdrawn it, and released a summary of a second note. Two off-site apps: one that
//   reaches the clinic's server, one that can't.
// Inputs and system conditions: GET /portal/summaries on each off-site app; the connected one's shipper run; then
//   POST .../withdraw on-site.
// Test data: The test's summary; the withdrawn one "We talked about the wrong visit.".
// Steps:
//   1. On the app that can't reach the clinic's server, open Visit summaries. Dr. Test then withdraws the standing
//      summary, and releases a third, of a third note.
//   2. On the connected app, open Visit summaries; its shipper runs. Dr. Test withdraws the third summary.
// Expected result: Step 1: the summary and a placeholder, with the read-only notice; the reads are queued, not in the
//   clinic's log, so the withdrawal emails nobody. Step 2: the summary's read arrives with source cloud, so the
//   withdrawal emails the patient.
// Creator: Tahseen Arnan
public class TC_UC6_20_VisitSummariesOffSite(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Off_site_summaries_are_read_from_the_cloud_copy_and_a_shipped_view_counts()
    {
        var (_, session, patient) = await PortalPatientAsync(apps, "Away");
        var first = await SignedNoteAsync(session.Client, patient.Patient);
        var second = await SignedNoteAsync(session.Client, patient.Patient);
        var third = await SignedNoteAsync(session.Client, patient.Patient);
        async Task<Guid> ReleaseOneAsync(Guid note, string talked = TalkedAbout)
        {
            using var released = await ReleaseAsync(session.Client, patient.Patient, note, talked);
            Assert.Contains("Released:", Words(await ReadAsync(released)));
            return await StandingAsync(apps, patient.Patient);
        }

        async Task WithdrawOneAsync(Guid note, Guid summary)
        {
            using var withdrawn = await WithdrawAsync(session.Client, patient.Patient, note, summary);
            Assert.Contains("Withdrawn:", Words(await ReadAsync(withdrawn)));
        }

        await WithdrawOneAsync(first, await ReleaseOneAsync(first, "We talked about the wrong visit."));
        var standing = await ReleaseOneAsync(second);
        var emails = EmailsTo(apps, patient.Email).Count;

        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));
        var cutOff = Words(await Client(apps.CloudCutOff, patient.Jar).GetStringAsync("/portal/summaries", Cancel));
        Assert.Contains(TalkedAbout, cutOff);
        Assert.Contains("was withdrawn by your care team.", cutOff);
        Assert.DoesNotContain("wrong visit", cutOff);
        Assert.Contains("Read-only: the clinic's server can't be reached", cutOff);
        var (queued, _) = apps.CloudCutOff.Services.GetRequiredService<CloudAuditQueue>().ReadPending(int.MaxValue);
        Assert.Contains(queued, e => e.Action == AuditActions.Read && e.TableName == "visit_summaries" && e.RowId == standing);
        // Not shipped yet: the clinic doesn't know of the view, so the withdrawal emails nobody (the known limit).
        await WithdrawOneAsync(second, standing);
        Assert.Equal(emails, EmailsTo(apps, patient.Email).Count);

        var shipped = await ReleaseOneAsync(third);
        emails = EmailsTo(apps, patient.Email).Count;
        Assert.Contains(TalkedAbout, await Client(apps.Cloud, patient.Jar).GetStringAsync("/portal/summaries", Cancel));
        await apps.ShipCloudAuditAsync();
        Assert.Equal(["cloud"], await ProxyTests.QueryAsync(apps,
            $"SELECT DISTINCT source FROM ehr.audit_events WHERE patient_id = @patient AND table_name = 'visit_summaries' AND action = 'read' AND row_id = '{shipped}'", patient.Patient));
        await WithdrawOneAsync(third, shipped);
        Assert.Equal(emails + 1, EmailsTo(apps, patient.Email).Count);
    }
}
