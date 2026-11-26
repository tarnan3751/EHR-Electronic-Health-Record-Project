using Ehr.Data.Reference;
using Ehr.Web.Areas.Portal.Pages.Forms;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.PreVisitFormTests;

namespace Ehr.Web.Tests.TestCases.UC22;

// Test Case ID: TC-UC22-02
// Purpose / scenario: Two other ways a form is answered. A patient answers the PHQ-9's question 9 ("Thoughts that you
//   would be better off dead or of hurting yourself in some way") other than "Not at all": the page they land on leads
//   with what to do now (911, the nearest emergency room, 988), without repeating the answer, and says so again whenever
//   the answers are opened; a GAD-7 never does. And someone the patient shares their record with, allowed sensitive
//   items, fills in a form for the patient: told whose answers they are giving, recorded as themselves, and shown the
//   same help about the patient when question 9 is answered so, worded for them throughout. In the portal, who sent a
//   form's answers is "you", "the patient" or "someone the record is shared with", never an account's name, even to
//   another proxy. Every help notice a questionnaire can name has its words, for the patient and for a proxy.
// Type: Functional (alternative flow)
// Requirements: proposed FR-59, FR-19
// Use case: UC 22, Answer a pre-visit form
// Preconditions: The clinician has the patient on their panel and two appointments booked with them, two and three
//   days ahead, with a PHQ-9 and a GAD-7 asked for before the first and a PHQ-9 before the second. The patient has
//   given access to their record, with sensitive items, to two other patients, "Synthetic Carer" and "Synthetic Second".
// Inputs and system conditions: POST /portal/forms/{form} as the patient and as the proxy; GET /portal/forms/{form}.
// Test data: Clinician "Dr. Forms Two". Question 9 "Several days", the others "Not at all"; the GAD-7 all "Nearly every
//   day".
// Steps:
//   1. As the patient, send the first PHQ-9, then open its answers again.
//   2. As the patient, send the GAD-7.
//   3. As the proxy, choose the patient's record and open the second PHQ-9; send it with the same answers.
//   4. Open the second PHQ-9's answers as the second proxy, the first and the patient; the first PHQ-9's as the first
//      proxy.
// Expected result: Step 1: both times, the page starts with "If you are thinking about hurting yourself, or feel you
//   might act on these thoughts, call 911 or go to the nearest emergency room now. You can call or text 988 at any time,
//   day or night, to talk with someone at the 988 Suicide & Crisis Lifeline. Your care team will read your answers
//   before your visit, but not as soon as you send them.", before "Answers sent:". Step 2: no such sentence. Step 3: the
//   form says "You are answering for Synthetic Booked." and "Not monitored: Synthetic Booked's care team reads this form
//   before the visit, not when you send it. If Synthetic Booked needs help now, call 911. ..."; once sent, the page
//   leads with "If Synthetic Booked is thinking about hurting themselves, or you think they might act on these thoughts,
//   call 911 ...", then the 988 sentence, then "Synthetic Booked's care team will read these answers before the visit,
//   but not as soon as you send them."; the answers are recorded as the proxy's account under the grant, and the
//   clinician sees them as a proxy's. Step 4: "by someone the record is shared with" to the second proxy and the
//   patient, "by you" to the first proxy, "by the patient" for the patient's own; no page names either proxy.
// Creator: Tahseen Arnan
public class TC_UC22_02_AlternativeFlow(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    const string Crisis = "call 911 or go to the nearest emergency room now. You can call or text 988 at any time, day or night, to talk with someone at "
        + "the 988 Suicide & Crisis Lifeline.";

    const string Help = Crisis + " Your care team will read your answers before your visit, but not as soon as you send them.";

    [Fact]
    public async Task An_answer_about_self_harm_leads_with_where_to_get_help_and_a_proxy_answers_as_themselves()
    {
        var visit = await VisitAsync(apps, "Dr. Forms Two");
        var later = await BookAsync(apps, visit, days: 3, time: "16:00");
        foreach (var (appointment, code) in new[] { (visit.Appointment, Phq9), (visit.Appointment, Gad7), (later, Phq9) })
        {
            using var asked = await AskAsync(visit.Session.Client, visit.Patient, appointment, code);
        }

        var (portal, _) = await PortalAsync(apps, visit);
        var first = await RequestAsync(apps, visit.Appointment, Phq9);
        using var sent = await SendAsync(portal, first, Answers(PhqItems, 0, 0, 0, 0, 0, 0, 0, 0, 1));
        var page = Text(await ReadAsync(sent));
        Assert.Contains("If you are thinking about hurting yourself, or feel you might act on these thoughts, " + Help, page);
        Assert.True(page.IndexOf("If you are thinking", StringComparison.Ordinal) < page.IndexOf("Answers sent:", StringComparison.Ordinal),
            "The help comes before the confirmation.");
        Assert.DoesNotContain("better off dead", page[..page.IndexOf("Not monitored:", StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.Contains("If you are thinking about hurting yourself", Text(await portal.GetStringAsync(FormPath(first), Cancel)));

        using var anxiety = await SendAsync(portal, await RequestAsync(apps, visit.Appointment, Gad7), Answers(GadItems, 3, 3, 3, 3, 3, 3, 3));
        Assert.DoesNotContain("911 or go to the nearest emergency room", Text(await ReadAsync(anxiety)));

        // The proxy: a patient of the clinic, given access with sensitive items by the patient, looking at their record.
        var proxy = await ProxyTests.PersonAsync(apps, "Synthetic", "Carer");
        var grant = await GrantAsync(apps, visit.Account, visit.Patient, proxy.Email, sensitive: true);
        await ProxyTests.ViewAsync(proxy, grant);
        var second = await RequestAsync(apps, later, Phq9);
        var forProxy = Text(await proxy.Client.GetStringAsync(FormPath(second), Cancel));
        Assert.Contains("You are answering for Synthetic Booked. The questions ask how they have been.", forProxy);
        Assert.Contains("Not monitored: Synthetic Booked's care team reads this form before the visit, not when you send it. If Synthetic Booked needs help now, "
            + "call 911. You can call or text 988 at any time to talk with someone at the 988 Suicide & Crisis Lifeline.", forProxy);
        using var byProxy = await SendAsync(proxy.Client, second, Answers(PhqItems, 0, 0, 0, 0, 0, 0, 0, 0, 1));
        var proxySent = Text(await ReadAsync(byProxy));
        Assert.Contains("If Synthetic Booked is thinking about hurting themselves, or you think they might act on these thoughts, " + Crisis
            + " Synthetic Booked's care team will read these answers before the visit, but not as soon as you send them.", proxySent);
        Assert.DoesNotContain("Your care team", proxySent, StringComparison.Ordinal);
        Assert.Equal([$"{proxy.Account} {grant}"], await QueryAsync(apps,
            "SELECT answered_by || ' ' || proxy_grant_id FROM ehr.questionnaire_responses WHERE request_id = @request", ("request", second)));
        Assert.Contains("(proxy), not the patient", Text(await visit.Session.Client.GetStringAsync(FormsPath(visit.Patient) + "?sensitive=true", Cancel)));

        // Who sent them, in the portal: never an account's name, even to another proxy with sensitive items. (The page
        // names the signed-in account itself, as every page does; never anyone else.)
        var other = await ProxyTests.PersonAsync(apps, "Synthetic", "Second");
        await ProxyTests.ViewAsync(other, await GrantAsync(apps, visit.Account, visit.Patient, other.Email, sensitive: true));
        foreach (var (reader, form, by, others) in new[]
                 {
                     (other.Client, second, "someone the record is shared with", new[] { "Carer" }),
                     (portal, second, "someone the record is shared with", ["Carer", "Second"]),
                     (proxy.Client, second, "you", ["Second"]),
                     (proxy.Client, first, "the patient", ["Second"]),
                 })
        {
            var shown = Text(await reader.GetStringAsync(FormPath(form), Cancel));
            Assert.Matches($"Sent [^.]+ UTC by {by}\\.", shown);
            Assert.DoesNotContain("(proxy)", shown, StringComparison.Ordinal);
            foreach (var name in others)
            {
                Assert.DoesNotContain(name, shown, StringComparison.Ordinal);
            }
        }

        // Every help notice a questionnaire can name is worded, for the patient and for a proxy.
        foreach (var help in QuestionnaireHelp.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(FormNotices.Help(help, null, sent: true)), help);
            Assert.Contains("Synthetic Booked", FormNotices.Help(help, "Synthetic Booked", sent: true), StringComparison.Ordinal);
        }
    }
}
