using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.RecordViewTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-44
// Purpose / scenario: Someone the patient shares their record with sees who viewed it as their access allows: the care
//   team's views and their own, each named by its page; views of sensitive items and of visit notes only when the
//   patient allowed them sensitive items; views of a conversation only when their access includes messages; and never
//   the patient's own views or another proxy's, not even as a line without a name.
// Type: Functional (alternative flow: proxies)
// Requirements: proposed FR-57, FR-19
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a confirmed portal account, signed in on the on-site app, and has given three other
//   patients of the clinic access: one with sensitive items and messages, one with messages and without sensitive
//   items, one with sensitive items and without messages; each is looking at the record. Dr. Test has the patient on
//   their panel.
// Inputs and system conditions: Dr. Test opens the chart, the chart with sensitive items, a note they signed, and a
//   conversation the patient started; each proxy GETs /portal/views; on the on-site app.
// Test data: The conversation "Synthetic question" with "Synthetic body".
// Steps:
//   1. Dr. Test makes the views.
//   2. Each proxy opens Who viewed this record.
// Expected result: Each page is titled "Who viewed this record", under the band naming Synthetic Shapes, has Dr. Test's
//   "Chart" (with sensitive items, for a proxy who may see them) and the proxy's own views of the portal's home as "You",
//   and no line of anyone else's. Sensitive items and messages: also "Visit note" and "Messages".
//   Messages only: no line with sensitive items and no visit note. Sensitive items only: no conversation.
// Creator: Tahseen Arnan
public class TC_UC6_44_WhoViewedAsAProxy(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_proxy_sees_the_care_teams_views_and_their_own_as_their_access_allows()
    {
        var (_, session, patient) = await ViewedPatientAsync(apps, "Shapes");
        var (full, _) = await VisitSummaryTests.ProxyForAsync(apps, patient, "Fullaccess", sensitive: true, scope: "record-and-messages");
        var (plain, _) = await VisitSummaryTests.ProxyForAsync(apps, patient, "Plainaccess", sensitive: false, scope: "record-and-messages");
        var (recordOnly, _) = await VisitSummaryTests.ProxyForAsync(apps, patient, "Recordaccess", sensitive: true);

        // Step 1.
        var chart = $"/patients/{patient.Patient}";
        await session.Client.GetStringAsync(chart, Cancel);
        await session.Client.GetStringAsync(chart + "?sensitive=true", Cancel);
        var note = await SignedNoteAsync(session.Client, patient.Patient);
        await session.Client.GetStringAsync(NotePath(patient.Patient, note), Cancel);
        var thread = await MessageTests.StartAsync(patient.Client, "Synthetic question", "Synthetic body");
        await session.Client.GetStringAsync($"{chart}/messages/{thread}", Cancel);

        // Step 2.
        const string Sensitive = "with sensitive items";
        const string Note = "| Visit note";
        const string Conversation = "| Messages";
        foreach (var (proxy, sensitive, messages) in new[] { (full, true, true), (plain, false, true), (recordOnly, true, false) })
        {
            var html = await PageAsync(proxy.Client);
            var lines = Lines(html);
            Assert.Contains("You're viewing Synthetic Shapes's record", Words(html));
            // The chart, opened with and without sensitive items in the same minute: one line, with them for a proxy who may
            // see them.
            Assert.Contains(lines, line => line.StartsWith("staff | Dr. Test, clinician | Chart", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.StartsWith("you | You | Home page", StringComparison.Ordinal));
            Assert.All(lines, line => Assert.Matches("^(staff|you) ", line));
            Assert.Equal(sensitive, lines.Any(line => line.Contains(Sensitive, StringComparison.Ordinal)));
            Assert.Equal(sensitive, lines.Any(line => line.Contains(Note, StringComparison.Ordinal)));
            Assert.Equal(messages, lines.Any(line => line.Contains(Conversation, StringComparison.Ordinal)));
            Assert.Contains("<h1>Who viewed this record</h1>", html);
        }
    }
}
