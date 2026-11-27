using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.MessageTests;

namespace Ehr.Web.Tests.TestCases.UC17;

// Test Case ID: TC-UC17-02
// Purpose / scenario: A clinician starts a conversation with a patient from the chart. And a clinician who isn't on
//   the patient's care team can read a conversation under emergency access, which is recorded, but can't reply, and
//   doesn't mark the patient's messages read for the care team.
// Type: Functional (alternative flow)
// Requirements: FR-18, proposed FR-43
// Use case: UC 17, Answer a patient's message
// Preconditions: One clinician has the patient on their panel. A second, "Dr. Covering Messages", doesn't. The patient
//   has a portal account.
// Inputs and system conditions: GET /patients/{id}; GET and POST /patients/{id}/messages/new and
//   /patients/{id}/messages/{thread}; on the on-site app.
// Test data: Subject "Synthetic: your lab results". Message "Synthetic: please book a follow-up." The patient's reply
//   "Synthetic: will do."
// Steps:
//   1. As the first clinician, follow the chart's "New message", and send the subject and message.
//   2. As the patient, reply.
//   3. As the second clinician, request the conversation; open the chart with a reason; open the conversation; post
//      a reply; request the new-message form.
// Expected result: Step 1: the conversation opens with "Sent."; one email tells the patient there is a message; no
//   inbox item is created (staff started it). Step 2: the first clinician gets an inbox item. Step 3: before a
//   reason, the request is answered with a redirect to the chart and no read is recorded; after, the conversation is
//   shown with "To reply, add this patient to My patients." and no form, its opening is a recorded read of the thread,
//   the patient's reply stays unread for the care team, and the post and the form request are each redirected to
//   the chart with nothing saved.
// Creator: Tahseen Arnan
public class TC_UC17_02_AlternativeFlow(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_clinician_starts_a_thread_and_emergency_access_reads_without_replying()
    {
        var (clinician, patient, staff, portal) = await CareAsync(apps, "Dr. Starts Messages");
        var chart = await staff.Client.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.Matches($"<a [^>]*data-new-message[^>]*href=\"/patients/{patient}/messages/new\"[^>]*>New message</a>", chart);
        Assert.Contains("No messages.", chart);

        var emails = apps.Emails.Sent.Count;
        using var started = await PostFormAsync(staff.Client, $"/patients/{patient}/messages/new", $"/patients/{patient}/messages/new",
            ("Subject", "Synthetic: your lab results"), ("Body", "Synthetic: please book a follow-up."));
        Assert.Contains("Sent.", Words(await ReadAsync(started)));
        var thread = Guid.Parse(started.RequestMessage!.RequestUri!.AbsolutePath.Split('/').Last());
        Assert.Equal(emails + 1, apps.Emails.Sent.Count);
        Assert.Empty(await OpenInboxAsync(apps, patient));

        using var answered = await PostFormAsync(portal.Client, $"/portal/messages/{thread}", $"/portal/messages/{thread}", ("Body", "Synthetic: will do."));
        Assert.Equal([clinician.Id.ToString()], await OpenInboxAsync(apps, patient));

        var covering = await apps.CreateStaffAsync("Dr. Covering Messages", StaffRoles.Clinician);
        var other = await SignInAsync(apps.OnPrem, covering);
        var noRedirects = Client(apps.OnPrem, other.Jar, followRedirects: false);
        using var before = await noRedirects.GetAsync($"/patients/{patient}/messages/{thread}", Cancel);
        Assert.Equal($"/patients/{patient}", new Uri(Base, before.Headers.Location!).AbsolutePath);
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.Read, covering.Id, patient));

        using var reason = await PostFormAsync(other.Client, $"/patients/{patient}/reason", $"/patients/{patient}/reason", ("Reason", "Covering the night shift"));
        Assert.DoesNotContain("data-new-message", await ReadAsync(reason));
        var reads = await apps.AuditEventsAsync(AuditActions.Read, covering.Id, patient);
        var page = await other.Client.GetStringAsync($"/patients/{patient}/messages/{thread}", Cancel);
        Assert.Contains("Synthetic: will do.", page);
        Assert.Contains("To reply, add this patient to My patients.", page);
        Assert.DoesNotContain("<textarea", page);
        Assert.Equal(reads + 1, await apps.AuditEventsAsync(AuditActions.Read, covering.Id, patient));
        Assert.Equal(["unread"], await RowsAsync(apps, patient, "CASE WHEN read_at IS NULL THEN 'unread' ELSE 'read' END", where: "sender_kind = 'patient'"));

        using var post = await noRedirects.PostAsync($"/patients/{patient}/messages/{thread}", Form(("__RequestVerificationToken", Token(page)), ("Body", "Synthetic: not mine to send")), Cancel);
        using var form = await noRedirects.GetAsync($"/patients/{patient}/messages/new", Cancel);
        Assert.Equal($"/patients/{patient}", new Uri(Base, post.Headers.Location!).AbsolutePath);
        Assert.Equal($"/patients/{patient}", new Uri(Base, form.Headers.Location!).AbsolutePath);
        Assert.Equal(2, (await RowsAsync(apps, patient, "body")).Count);
    }
}
