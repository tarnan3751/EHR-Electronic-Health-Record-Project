using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.RecordViewTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-40
// Purpose / scenario: The patient reads who viewed their record, from the portal's home: newest first, each view saying
//   who (a staff member's name and role, or "You"), which part, and when, in the portal's date-and-time format, over
//   the last 12 months, which the page says. Looking is itself a recorded view, listed from the next look on.
// Type: Functional (successful flow: who viewed my record)
// Requirements: proposed FR-57
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a confirmed portal account and is signed in on the on-site app, which opened their
//   home. Dr. Test signed in, then put the patient on their panel.
// Inputs and system conditions: Dr. Test opens the patient's chart; the patient opens the portal's home, then GET
//   /portal/views twice, on the on-site app.
// Test data: None beyond the accounts.
// Steps:
//   1. Dr. Test opens the chart. The patient opens the home and follows "Who viewed my record".
//   2. The patient opens the page again.
// Expected result: Step 1: the page sends Cache-Control: no-store and says "in the last 12 months, newest first"; its
//   lines are "You, Your portal home page, with your appointments" (the home), "Dr. Test, clinician, Your chart, with
//   the list of your visit notes" at the chart's time as "MMM d, yyyy, HH:mm UTC", then "You, Your portal
//   home page, with your appointments" (the home after signing in). Opening it is one recorded read by the patient's
//   own account, of the patient's part of the audit log, with no row. Step 2: the first line is "You, Who viewed your
//   record".
// Creator: Tahseen Arnan
public class TC_UC6_40_WhoViewedMyRecord(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_patient_reads_who_viewed_their_record_newest_first_and_their_look_is_recorded()
    {
        var (clinician, session, patient) = await ViewedPatientAsync(apps, "Viewed");
        using (var chart = await session.Client.GetAsync($"/patients/{patient.Patient}", Cancel))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, chart.StatusCode);
        }

        var charted = await LatestAsync(apps, patient.Patient, clinician.Id);
        var home = await patient.Client.GetStringAsync("/portal", Cancel);
        Assert.Contains("href=\"/portal/views\"", home);
        Assert.Contains("Who viewed my record", home);
        var readsBefore = await apps.AuditEventsAsync(AuditActions.Read, patient.Account, patient.Patient);

        // Step 1.
        using var response = await patient.Client.GetAsync(ViewsPath, Cancel);
        var html = await Browser.ReadAsync(response);

        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("in the last 12 months, newest first", Browser.Words(html));
        var lines = Lines(html);
        Assert.Equal(
            [
                "you | You | Your portal home page, with your appointments",
                $"staff | Dr. Test, clinician | Your chart, with the list of your visit notes | {Shown(charted)}",
                "you | You | Your portal home page, with your appointments",
            ],
            lines.Select(line => line.StartsWith("staff", StringComparison.Ordinal) ? line : string.Join(" | ", line.Split(" | ").Take(3))));
        Assert.All(lines, line => Assert.Matches(@"\| [A-Z][a-z]{2} \d{1,2}, \d{4}, \d{2}:\d{2} UTC$", line));
        Assert.Equal(readsBefore + 1, await apps.AuditEventsAsync(AuditActions.Read, patient.Account, patient.Patient));
        Assert.Equal(["audit_events no row onprem"], await ProxyTests.QueryAsync(apps,
            $"""
            SELECT table_name || ' ' || coalesce(row_id::text, 'no row') || ' ' || source FROM ehr.audit_events
            WHERE patient_id = @patient AND actor_id = '{patient.Account}' AND action = 'read' AND table_name = 'audit_events'
            """, patient.Patient));

        // Step 2.
        Assert.StartsWith("you | You | Who viewed your record | ", Lines(await PageAsync(patient.Client))[0], StringComparison.Ordinal);
    }
}
