using System.Net;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.PreVisitFormTests;

namespace Ehr.Web.Tests.TestCases.UC22;

// Test Case ID: TC-UC22-03
// Purpose / scenario: What a form refuses. A question left without an answer, or answered with something that isn't one
//   of its answers, is asked again with the other answers kept; a score sent with the answers changes nothing; a form
//   sent once can't be sent again; a form not open yet, or closed because its appointment was cancelled, can't be sent,
//   and the page says so; another patient's form doesn't exist.
// Type: Functional (invalid input)
// Requirements: proposed FR-59
// Use case: UC 22, Answer a pre-visit form
// Preconditions: The clinician has the patient on their panel and three appointments booked with them: two days ahead,
//   ten days ahead, and four days ahead (then cancelled), each with a PHQ-9 asked for. Another patient of another
//   clinician has a PHQ-9 asked for too. The patient is signed in to the portal.
// Inputs and system conditions: GET and POST /portal/forms/{form}; on the on-site app.
// Test data: Clinician "Dr. Forms Three". Question 9 left out; question 1 answered with a GAD-7 answer code that isn't
//   a PHQ-9 answer's; a field "Score" of 0 and "Band" of "Minimal" sent beside answers totalling 27.
// Steps:
//   1. Send the first PHQ-9 without question 9, and with a made-up answer to question 1.
//   2. Send it with every question answered "Nearly every day", and with Score and Band.
//   3. Send it again.
//   4. Open and send the PHQ-9 ten days ahead, and the cancelled one's.
//   5. Open and send the other patient's PHQ-9.
// Expected result: Step 1: 422, "Not sent. Answer the highlighted questions and try again.", "Choose an answer." beside the
//   two questions, the cursor on the first of them, the other answers still chosen; nothing stored. Step 2: stored
//   with the database's score of 27, Severe, needing follow-up. Step 3: the answers page; still one set of answers.
//   Step 4: the first says when it can be filled in and has no form to send; the cancelled one says it can no longer be
//   filled in; sending either is 409, "Not sent." with why ("This form
//   can't be filled in yet." and "This form can no longer be filled in."), and stores nothing. Step 5: 404 for both.
// Creator: Tahseen Arnan
public class TC_UC22_03_InvalidInput(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Missing_or_made_up_answers_a_second_send_and_forms_that_arent_open_or_arent_theirs_are_refused()
    {
        var visit = await VisitAsync(apps, "Dr. Forms Three");
        var early = await BookAsync(apps, visit, days: 10, time: "16:00");
        var cancelled = await BookAsync(apps, visit, days: 4, time: "16:00");
        foreach (var appointment in new[] { visit.Appointment, early, cancelled })
        {
            using var asked = await AskAsync(visit.Session.Client, visit.Patient, appointment, Phq9);
        }

        using (var cancel = await AppointmentTests.ChangeAsync(visit.Session.Client, cancelled, "Cancel", ("CancelReason", "Synthetic: moved")))
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        }

        var (portal, _) = await PortalAsync(apps, visit);
        var request = await RequestAsync(apps, visit.Appointment, Phq9);
        var incomplete = Answers(PhqItems, 2, 2, 2, 2, 2, 2, 2, 2, 0);
        incomplete.Remove("44260-8");
        incomplete["44250-9"] = "LA6568-5x";
        using (var missing = await SendAsync(portal, request, incomplete))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
            var html = await missing.Content.ReadAsStringAsync(Cancel);
            Assert.Contains("Not sent. Answer the highlighted questions and try again.", Text(html));
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "Choose an answer.").Count);
            Assert.Matches("id=\"question-44250-9-0\"[^>]*autofocus", html);
            Assert.Matches("value=\"LA6570-1\" checked=\"checked\"", html);
        }

        Assert.Empty(await QueryAsync(apps, "SELECT id::text FROM ehr.questionnaire_responses WHERE request_id = @request", ("request", request)));

        using (var forged = await SendAsync(portal, request, Answers(PhqItems, 3, 3, 3, 3, 3, 3, 3, 3, 3), ("Score", 0), ("Band", "Minimal")))
        {
            Assert.Equal(HttpStatusCode.OK, forged.StatusCode);
        }

        Assert.Equal(["27 Severe true"], await QueryAsync(apps,
            "SELECT score || ' ' || band || ' ' || follow_up FROM ehr.questionnaire_responses WHERE request_id = @request", ("request", request)));
        using (var again = await SendAsync(portal, request, Answers(PhqItems, 0, 0, 0, 0, 0, 0, 0, 0, 0)))
        {
            Assert.Equal(FormPath(request), PathOf(again));
            Assert.Contains("Sent", Text(await ReadAsync(again)));
        }

        Assert.Single(await QueryAsync(apps, "SELECT id::text FROM ehr.questionnaire_responses WHERE request_id = @request", ("request", request)));

        var notYet = await RequestAsync(apps, early, Phq9);
        var notYetPage = Text(await portal.GetStringAsync(FormPath(notYet), Cancel));
        Assert.Contains("You can fill it in from", notYetPage);
        Assert.DoesNotContain("Send to my care team", notYetPage);
        var closed = await RequestAsync(apps, cancelled, Phq9);
        Assert.Contains("This form can no longer be filled in.", Text(await portal.GetStringAsync(FormPath(closed), Cancel)));
        foreach (var (shut, why) in new[] { (notYet, "This form can't be filled in yet."), (closed, "This form can no longer be filled in.") })
        {
            using var refused = await SendAsync(portal, shut, Answers(PhqItems, 0, 0, 0, 0, 0, 0, 0, 0, 0));
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("Not sent. " + why, Text(await refused.Content.ReadAsStringAsync(Cancel)));
        }

        Assert.Empty(await QueryAsync(apps, "SELECT id::text FROM ehr.questionnaire_responses WHERE request_id IN (@a, @b)", ("a", notYet), ("b", closed)));

        var other = await VisitAsync(apps, "Dr. Forms Elsewhere");
        using (var asked = await AskAsync(other.Session.Client, other.Patient, other.Appointment, Phq9))
        {
            Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
        }

        var theirs = await RequestAsync(apps, other.Appointment, Phq9);
        using (var look = await portal.GetAsync(FormPath(theirs), Cancel))
        {
            Assert.Equal(HttpStatusCode.NotFound, look.StatusCode);
        }

        using (var send = await SendAsync(portal, theirs, Answers(PhqItems, 0, 0, 0, 0, 0, 0, 0, 0, 0)))
        {
            Assert.Equal(HttpStatusCode.NotFound, send.StatusCode);
        }
    }
}
