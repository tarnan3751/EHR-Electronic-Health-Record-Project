using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.MessageTests;

namespace Ehr.Web.Tests.TestCases.UC17;

// Test Case ID: TC-UC17-01
// Purpose / scenario: A patient's message arrives in the inbox of each clinician on their care team. One of them opens
//   the conversation from the inbox, reads it and replies; the item is then done for the whole team.
// Type: Functional (successful flow)
// Requirements: FR-18, proposed FR-43
// Use case: UC 17, Answer a patient's message
// Preconditions: Two clinicians have the patient on their panels and are signed in on the on-site app. The patient
//   has sent a message from the portal.
// Inputs and system conditions: GET /inbox; GET and POST /patients/{id}/messages/{thread}; GET /patients/{id}; on the
//   on-site app.
// Test data: Clinicians "Dr. Care One" and "Dr. Care Two". Subject "Synthetic: refill question". Message "Synthetic:
//   can I get a refill?". Reply "Synthetic: yes, sent to your pharmacy."
// Steps:
//   1. As each clinician, open the inbox.
//   2. As the first, follow "Open the conversation".
//   3. Reply.
//   4. As the second, open the inbox; then open the chart.
// Expected result: Step 1: each inbox shows one "Message" item, "A patient sent a message", with the patient's name
//   and a link to the conversation; the count in the app bar is 1. Step 2: the conversation shows the patient's banner
//   and the message from the patient by name; opening it is a recorded read of the thread and marks the message read.
//   Step 3: the page says "Sent." and shows the reply under the first clinician's name; one email tells the patient,
//   and a second reply before they have read the first sends no second email. Step 4: the second clinician's
//   inbox is empty; the chart's Messages section lists the conversation, not marked new.
// Creator: Tahseen Arnan
public class TC_UC17_01_SuccessfulFlow(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_care_team_clinician_answers_from_the_inbox_and_the_item_is_done_for_the_team()
    {
        var (first, patient, staff, portal) = await CareAsync(apps, "Dr. Care One");
        var second = await apps.CreateStaffAsync("Dr. Care Two", StaffRoles.Clinician);
        await apps.AssignAsync(second, patient);
        var colleague = await SignInAsync(apps.OnPrem, second);
        var thread = await StartAsync(portal.Client, "Synthetic: refill question", "Synthetic: can I get a refill?");

        foreach (var session in new[] { staff, colleague })
        {
            var inbox = await session.Client.GetStringAsync("/inbox", Cancel);
            Assert.Contains("<span class=\"sc-tag\">Message</span> A patient sent a message", inbox);
            Assert.Matches($"<a [^>]*data-inbox-source[^>]*href=\"/patients/{patient}/messages/{thread}\"[^>]*>Open the conversation</a>", inbox);
            Assert.Contains("Messaged, Synthetic", inbox);
            Assert.DoesNotContain("refill", inbox);
            Assert.Contains("data-inbox-count", inbox);
        }

        var reads = await apps.AuditEventsAsync(AuditActions.Read, first.Id, patient);
        var conversation = await staff.Client.GetStringAsync($"/patients/{patient}/messages/{thread}", Cancel);
        Assert.Contains("<h1>Synthetic: refill question</h1>", conversation);
        Assert.Contains("<strong>Synthetic Messaged</strong>", conversation);
        Assert.Contains("Synthetic: can I get a refill?", conversation);
        Assert.Contains("class=\"sc-banner\"", conversation);
        Assert.Equal(reads + 1, await apps.AuditEventsAsync(AuditActions.Read, first.Id, patient));
        Assert.Equal(["patient read"], await RowsAsync(apps, patient, "sender_kind || CASE WHEN read_at IS NULL THEN ' unread' ELSE ' read' END"));

        var emails = apps.Emails.Sent.Count;
        using var reply = await PostFormAsync(staff.Client, $"/patients/{patient}/messages/{thread}", $"/patients/{patient}/messages/{thread}",
            ("Body", "Synthetic: yes, sent to your pharmacy."));
        var sent = await ReadAsync(reply);
        Assert.Contains("Sent.", Words(sent));
        Assert.Contains("<strong>Dr. Care One</strong>", sent);
        Assert.Equal([$"staff {first.Id}"], await RowsAsync(apps, patient, "sender_kind || ' ' || sender_id", where: "sender_kind = 'staff'"));

        Assert.Equal(emails + 1, apps.Emails.Sent.Count);
        using var more = await PostFormAsync(staff.Client, $"/patients/{patient}/messages/{thread}", $"/patients/{patient}/messages/{thread}",
            ("Body", "Synthetic: and it will be ready tomorrow."));
        Assert.Contains("Synthetic: and it will be ready tomorrow.", await ReadAsync(more));
        Assert.Equal(emails + 1, apps.Emails.Sent.Count);

        Assert.Contains("Nothing needs your action.", await colleague.Client.GetStringAsync("/inbox", Cancel));
        Assert.Empty(await OpenInboxAsync(apps, patient));
        var chart = await colleague.Client.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.Contains($"<a href=\"/patients/{patient}/messages/{thread}\">Synthetic: refill question</a>", chart);
        Assert.DoesNotContain("sc-tag--info\">New", chart);
    }
}
