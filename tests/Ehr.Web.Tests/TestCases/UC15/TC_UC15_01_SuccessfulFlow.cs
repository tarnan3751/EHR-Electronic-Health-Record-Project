using Ehr.Data.Audit;
using Ehr.Data.Inbox;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.InboxTests;

namespace Ehr.Web.Tests.TestCases.UC15;

// Test Case ID: TC-UC15-01
// Purpose / scenario: A clinician with items in their inbox sees how many in the app bar on every clinical page, opens
//   the inbox, sees each item with its kind, its patient and a link to the chart, oldest first, and marks one done.
// Type: Functional (successful flow)
// Requirements: proposed FR-41
// Use case: UC 15, Work through the inbox
// Preconditions: The clinician is signed in on the on-site app with a patient on their panel. Their inbox holds two
//   items about that patient. The clinic's server is up.
// Inputs and system conditions: GET /patients, GET /inbox, POST /inbox?handler=Done&id={item}, GET /inbox?done=true,
//   on the on-site app.
// Test data: Clinician "Dr. Inbox One". Patient Synthetic Inboxed. Items: a message "Synthetic: question about a
//   refill" (received 30 minutes ago) and a result "Synthetic: lipid panel is back" (received 5 minutes ago).
// Steps:
//   1. Open the patients page.
//   2. Open the inbox.
//   3. Choose "Mark done" on the message.
//   4. Open the "Done" view.
// Expected result: Step 1: the app bar's Inbox link shows 2. Step 2: the page lists the message first, then the
//   result, each with its kind as a tag, the patient's name linked to their chart and their MRN, and a "Mark done"
//   button; the patient's name being shown is one recorded read of the patient. Step 3: the inbox lists only the
//   result and the app bar shows 1; the item has a done time, and the write audit names the clinician. Step 4: the
//   message is listed as done.
// Creator: Tahseen Arnan (expansion step 3); reviewed and run by Samir Mukshe, approving PR 2
public class TC_UC15_01_SuccessfulFlow(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_clinician_sees_the_count_works_through_the_inbox_and_marks_an_item_done()
    {
        var (clinician, patient, session) = await ClinicianAsync(apps, "Dr. Inbox One");
        var message = await AddAsync(apps, clinician.Id, "Synthetic: question about a refill", patient, InboxItemKind.Message, minutesAgo: 30);
        await AddAsync(apps, clinician.Id, "Synthetic: lipid panel is back", patient, InboxItemKind.Result, minutesAgo: 5);

        var home = await session.Client.GetStringAsync("/patients", Cancel);
        Assert.Matches("<a [^>]*href=\"/inbox\"[^>]*>\\s*Inbox\\s*<span class=\"sc-appbar__count\" data-inbox-count>", home);
        Assert.Matches(@"Inbox\s*,\s*2\s*to do", Words(home));

        var reads = await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient);
        var inbox = await session.Client.GetStringAsync("/inbox", Cancel);
        Assert.Contains("<h1>Inbox</h1>", inbox);
        Assert.True(inbox.IndexOf("question about a refill", StringComparison.Ordinal) < inbox.IndexOf("lipid panel is back", StringComparison.Ordinal));
        Assert.Contains("<span class=\"sc-tag\">Message</span> Synthetic: question about a refill", inbox);
        Assert.Contains("<span class=\"sc-tag\">Result to acknowledge</span>", inbox);
        Assert.Contains($"<a href=\"/patients/{patient}\">Inboxed, Synthetic</a>", inbox);
        Assert.Contains("aria-label=\"Mark done: Synthetic: question about a refill\">Mark done</button>", inbox);
        Assert.DoesNotContain("htmx.min.js", inbox);
        // One patient named, once: one recorded read.
        Assert.Equal(reads + 1, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient));

        using var done = await MarkDoneAsync(session.Client, message);
        var after = await ReadAsync(done);
        Assert.DoesNotContain("question about a refill", after);
        Assert.Contains("lipid panel is back", after);
        Assert.Matches(@"Inbox\s*,\s*1\s*to do", Words(after));
        Assert.True(await IsDoneAsync(apps, message));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Update, clinician.Id, patient));

        var finished = await session.Client.GetStringAsync("/inbox?done=true", Cancel);
        Assert.Contains("question about a refill", finished);
        Assert.DoesNotContain("lipid panel is back", finished);
        Assert.DoesNotContain("Mark done</button>", finished);
    }
}
