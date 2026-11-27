using System.Net;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.MessageTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-08
// Purpose / scenario: A patient writes to their care team from the portal and reads the reply. The message reaches the
//   care team's inbox; the reply shows as new, then as read; the email about it says nothing of its content.
// Type: Functional (alternative flow: step 3 alternative, send a secure message to the care team)
// Requirements: FR-18
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a portal account and is on one clinician's panel. Both are signed in. The clinic's
//   server is up.
// Inputs and system conditions: GET and POST /portal/messages/new; GET /portal/messages and /portal/messages/{thread};
//   POST /patients/{id}/messages/{thread} as the clinician; on the on-site app.
// Test data: Clinician "Dr. Portal Messages". Subject "Synthetic: question about my inhaler". Message "Synthetic: how
//   often should I use it?". Reply "Synthetic: two puffs every four hours as needed."
// Steps:
//   1. In the portal, open "Messages", then "New message", and send the subject and message.
//   2. As the clinician, reply in the conversation.
//   3. In the portal, open "Messages", then the conversation.
//   4. Open "Messages" again.
// Expected result: Step 1: the conversation opens with "Sent." and the message from "You"; one item is in the
//   clinician's inbox, saying only "A patient sent a message". Step 2: one email goes to the patient's address:
//   subject "New message from your clinic", a button "Read your message" to /portal/messages, and none of
//   the subject, the message or the reply in it. Step 3: the list marks the conversation "New"; the conversation
//   shows the reply from "Your care team", with no staff name; opening it is a recorded read of the thread and marks
//   the reply read. Step 4: the conversation is no longer "New".
// Creator: Tahseen Arnan
public class TC_UC6_08_SendMessage(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_patient_writes_to_their_care_team_and_reads_the_reply()
    {
        var (clinician, patient, staff, portal) = await CareAsync(apps, "Dr. Portal Messages");
        var list = await portal.Client.GetStringAsync("/portal/messages", Cancel);
        Assert.Contains("No messages.", list);
        Assert.Contains("href=\"/portal/messages/new\"", list);
        Assert.Contains("For an emergency, call your local emergency number.", list);

        var thread = await StartAsync(portal.Client, "Synthetic: question about my inhaler", "Synthetic: how often should I use it?");
        var opened = await portal.Client.GetStringAsync($"/portal/messages/{thread}?sent=true", Cancel);
        Assert.Contains("Sent. Replies from your care team appear here.", Words(opened));
        Assert.Contains("<strong>You</strong>", opened);
        Assert.Contains("Synthetic: how often should I use it?", opened);
        Assert.Equal([clinician.Id.ToString()], await OpenInboxAsync(apps, patient));
        Assert.Equal(["A patient sent a message"], await RowsAsync(apps, patient, "summary", "inbox_items", "created_at"));

        var emails = apps.Emails.Sent.Count;
        using var reply = await PostFormAsync(staff.Client, $"/patients/{patient}/messages/{thread}", $"/patients/{patient}/messages/{thread}",
            ("Body", "Synthetic: two puffs every four hours as needed."));
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Equal(emails + 1, apps.Emails.Sent.Count);
        var email = apps.Emails.Sent.Last();
        Assert.Equal("New message from your clinic", email.Subject);
        Assert.Equal("Read your message", apps.Emails.Actions[email.Body].Label);
        Assert.EndsWith("/portal/messages", apps.Emails.Actions[email.Body].Link);
        foreach (var secret in new[] { "inhaler", "puffs", "Dr. Portal Messages", "Messaged" })
        {
            Assert.DoesNotContain(secret, email.Subject + email.Body + apps.Emails.Actions[email.Body].Link, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("data-thread=\"new\"", await portal.Client.GetStringAsync("/portal/messages", Cancel));
        var reads = await apps.AuditEventsAsync(AuditActions.Read, patientId: patient);
        var conversation = await portal.Client.GetStringAsync($"/portal/messages/{thread}", Cancel);
        Assert.Contains("<strong>Your care team</strong>", conversation);
        Assert.Contains("Synthetic: two puffs every four hours as needed.", conversation);
        Assert.DoesNotContain("Dr. Portal Messages", conversation);
        Assert.Equal(reads + 1, await apps.AuditEventsAsync(AuditActions.Read, patientId: patient));
        Assert.Equal(["patient read", "staff read"], await RowsAsync(apps, patient, "sender_kind || CASE WHEN read_at IS NULL THEN ' unread' ELSE ' read' END"));
        Assert.Contains("data-thread=\"read\"", await portal.Client.GetStringAsync("/portal/messages", Cancel));
        Assert.Empty(await OpenInboxAsync(apps, patient));
    }
}
