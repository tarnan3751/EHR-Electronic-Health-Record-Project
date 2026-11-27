using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-16
// Purpose / scenario: Someone the patient shares their record with reads its visit summaries as their access allows:
//   a sensitive one only when the patient allowed them sensitive items, and then only after asking; without that, a
//   sensitive summary doesn't exist for them (no count, no notice, the same page as if there were none), and they lose
//   a summary the moment it becomes sensitive. Whatever they read is recorded under their own account and the patient.
// Type: Functional (alternative flow: proxies)
// Requirements: FR-19, proposed FR-44, proposed FR-55
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a portal account and has given access to three people: one with sensitive items
//   ("record"), one without ("record"), and one without but with messages ("record-and-messages"). Each is signed in
//   and looking at the patient's record. Dr. Test has the patient on their panel and has signed two notes.
// Inputs and system conditions: GET /portal/summaries, with and without ?sensitive=true, as each proxy, on the
//   on-site app, as Dr. Test releases and marks summaries.
// Test data: The test's summary of the first note; a second, ticked "Sensitive", of the second note.
// Steps:
//   1. Dr. Test releases the first summary. Each proxy opens Visit summaries; the proxy without sensitive items keeps
//      the page.
//   2. Dr. Test releases the second, sensitive, summary. Each proxy opens Visit summaries again; those without sensitive
//      items also ask for sensitive summaries by the address.
//   3. The proxy with sensitive items follows "Show sensitive summaries".
//   4. Dr. Test marks the first note sensitive. The proxies without sensitive items open Visit summaries.
// Expected result: Step 1: each sees the first summary. Step 2: the proxies without sensitive items get exactly the
//   page they had in step 1, asking or not (their form tokens aside); the proxy with them sees "Sensitive summaries
//   hidden. 1 visit summary is not shown." Step 3: both summaries, the second tagged "Sensitive", and "Sensitive
//   summaries shown. Your view is logged.", recorded as a view of sensitive items under the proxy's account and the patient.
//   Step 4: the proxies without sensitive items see "No visit summaries yet." and nothing of either.
// Creator: Tahseen Arnan
public class TC_UC6_16_VisitSummariesProxies(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_proxy_reads_the_summaries_their_access_allows_and_gets_no_hint_of_the_rest()
    {
        var (_, session, patient) = await PortalPatientAsync(apps, "Shares");
        var (allowed, _) = await ProxyForAsync(apps, patient, "Allowed", sensitive: true);
        var (without, _) = await ProxyForAsync(apps, patient, "Without", sensitive: false);
        var (messaging, _) = await ProxyForAsync(apps, patient, "Messaging", sensitive: false, scope: "record-and-messages");
        var first = await SignedNoteAsync(session.Client, patient.Patient);
        var second = await SignedNoteAsync(session.Client, patient.Patient);
        using (var released = await ReleaseAsync(session.Client, patient.Patient, first))
        {
            Assert.Contains("Released.", Words(await ReadAsync(released)));
        }

        var before = new Dictionary<ProxyTests.Person, string>();
        foreach (var proxy in new[] { allowed, without, messaging })
        {
            var page = await PortalAsync(proxy.Client);
            Assert.Contains(TalkedAbout, page);
            Assert.DoesNotContain("your care team", Words(page));
            before[proxy] = WithoutTokens(page);
        }

        using (var released = await ReleaseAsync(session.Client, patient.Patient, second, "We talked about something private.", sensitive: true))
        {
            Assert.Contains("Released.", Words(await ReadAsync(released)));
        }

        // No hint, asking or not: the same page as before the sensitive one existed.
        foreach (var proxy in new[] { without, messaging })
        {
            Assert.Equal(before[proxy], WithoutTokens(await PortalAsync(proxy.Client)));
            Assert.Equal(before[proxy], WithoutTokens(await PortalAsync(proxy.Client, sensitive: true)));
        }

        var hidden = await PortalAsync(allowed.Client);
        Assert.Contains("Sensitive summaries hidden. 1 visit summary is not shown. Show sensitive summaries", Words(hidden));
        Assert.Contains("href=\"/portal/summaries?sensitive=true\"", hidden);
        Assert.DoesNotContain("something private", hidden);
        var viewsBefore = await apps.AuditEventsAsync(AuditActions.ReadSensitive, allowed.Account, patient.Patient);
        var shown = await PortalAsync(allowed.Client, sensitive: true);
        Assert.Contains("We talked about something private.", shown);
        Assert.Contains(TalkedAbout, shown);
        Assert.Contains(">Sensitive</span>", shown);
        Assert.Contains("Sensitive summaries shown. Your view is logged.", Words(shown));
        Assert.Equal(viewsBefore + 1, await apps.AuditEventsAsync(AuditActions.ReadSensitive, allowed.Account, patient.Patient));

        // The first note marked: its summary is sensitive with it, and gone at once for the proxies without them.
        using (var marked = await ChangeSensitivityAsync(session.Client, patient.Patient, first, mark: true, "Discussed a private matter."))
        {
            Assert.Contains("Saved.", Words(await ReadAsync(marked)));
        }

        foreach (var proxy in new[] { without, messaging })
        {
            var gone = Words(await PortalAsync(proxy.Client));
            Assert.Contains("No visit summaries yet.", gone);
            Assert.DoesNotContain(TalkedAbout, gone);
            Assert.DoesNotContain("Sensitive", gone);
        }

        Assert.Contains(TalkedAbout, await PortalAsync(allowed.Client, sensitive: true));
    }
}
