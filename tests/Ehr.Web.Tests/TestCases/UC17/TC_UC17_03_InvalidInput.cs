using System.Net;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.MessageTests;

namespace Ehr.Web.Tests.TestCases.UC17;

// Test Case ID: TC-UC17-03
// Purpose / scenario: A message with nothing in it, or too long, isn't sent: the field is highlighted and what was
//   typed stays. A conversation asked for under the wrong patient's address is not found.
// Type: Functional (invalid input)
// Requirements: FR-18, proposed FR-43
// Use case: UC 17, Answer a patient's message
// Preconditions: The clinician is signed in on the on-site app with two patients on their panel; the first has a
//   conversation.
// Inputs and system conditions: POST /patients/{id}/messages/new and /patients/{id}/messages/{thread}; GET
//   /patients/{other}/messages/{thread}; on the on-site app.
// Test data: A subject of 151 letters; an empty message; a message of 4,001 letters; a reply of spaces.
// Steps:
//   1. Send a new message with the long subject and no message; then with a subject and the long message.
//   2. Reply with only spaces.
//   3. Request the first patient's conversation under the second patient's address, and post a reply there.
// Expected result: Step 1: each is answered 422 with "Not sent." and, in turn, "Shorten the subject to 150 characters
//   or fewer." with "Write your message.", then "Shorten the message to 4000 characters or fewer."; the subject typed
//   is still in its field. Step 2: 422 with "Write your message." Step 3: both are answered 404. No message or email
//   was added by any of them.
// Creator: Tahseen Arnan
public class TC_UC17_03_InvalidInput(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_empty_or_overlong_message_is_not_sent_and_a_thread_under_another_patient_is_not_found()
    {
        var (clinician, patient, staff, portal) = await CareAsync(apps, "Dr. Invalid Messages");
        var other = await apps.InsertPatientAsync("Synthetic", "Another");
        await apps.AssignAsync(clinician, other);
        var thread = await StartAsync(portal.Client, "Synthetic: a question", "Synthetic: hello");
        var emails = apps.Emails.Sent.Count;
        var path = $"/patients/{patient}/messages/new";

        using var longSubject = await PostFormAsync(staff.Client, path, path, ("Subject", new string('s', 151)), ("Body", ""));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, longSubject.StatusCode);
        var first = await longSubject.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("Not sent.", first);
        Assert.Contains("Shorten the subject to 150 characters or fewer.", first);
        Assert.Contains("Write your message.", first);
        using var longBody = await PostFormAsync(staff.Client, path, path, ("Subject", "Synthetic: kept"), ("Body", new string('b', 4001)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, longBody.StatusCode);
        var second = await longBody.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("Shorten the message to 4000 characters or fewer.", second);
        Assert.Contains("name=\"Subject\" value=\"Synthetic: kept\"", second);

        using var blank = await PostFormAsync(staff.Client, $"/patients/{patient}/messages/{thread}", $"/patients/{patient}/messages/{thread}", ("Body", "   "));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.StatusCode);
        Assert.Contains("Write your message.", await blank.Content.ReadAsStringAsync(Cancel));

        using var crossed = await staff.Client.GetAsync($"/patients/{other}/messages/{thread}", Cancel);
        Assert.Equal(HttpStatusCode.NotFound, crossed.StatusCode);
        var token = Token(await staff.Client.GetStringAsync($"/patients/{patient}/messages/{thread}", Cancel));
        using var posted = await staff.Client.PostAsync($"/patients/{other}/messages/{thread}", Form(("__RequestVerificationToken", token), ("Body", "Synthetic: misfiled")), Cancel);
        Assert.Equal(HttpStatusCode.NotFound, posted.StatusCode);

        Assert.Single(await RowsAsync(apps, patient, "body"));
        Assert.Empty(await RowsAsync(apps, other, "body"));
        Assert.Equal(emails, apps.Emails.Sent.Count);
    }
}
