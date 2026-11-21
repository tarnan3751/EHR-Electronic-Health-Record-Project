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
//   same help about the patient when question 9 is answered so.
// Type: Functional (alternative flow)
// Requirements: proposed FR-59, FR-19
// Use case: UC 22, Answer a pre-visit form
// Preconditions: The clinician has the patient on their panel and two appointments booked with them, two and three
//   days ahead, with a PHQ-9 and a GAD-7 asked for before the first and a PHQ-9 before the second. The patient has
//   given access to their record, with sensitive items, to another patient, "Synthetic Carer".
// Inputs and system conditions: POST /portal/forms/{form} as the patient and as the proxy; GET /portal/forms/{form}.
// Test data: Clinician "Dr. Forms Two". Question 9 "Several days", the others "Not at all"; the GAD-7 all "Nearly every
//   day".
// Steps:
//   1. As the patient, send the first PHQ-9, then open its answers again.
//   2. As the patient, send the GAD-7.
//   3. As the proxy, choose the patient's record and open the second PHQ-9; send it with the same answers.
// Expected result: Step 1: both times, the page starts with "If you are thinking about hurting yourself, or feel you
//   might act on these thoughts, call 911 or go to the nearest emergency room now. You can call or text 988 at any time,
//   day or night, to talk with someone at the 988 Suicide & Crisis Lifeline. Your care team will read your answers
//   before your visit, but not as soon as you send them.", before "Answers sent:". Step 2: no such sentence. Step 3: the
//   form says "You are answering for Synthetic Booked."; once sent, the page leads with "If Synthetic Booked is
//   thinking about hurting themselves, or you think they might act on these thoughts, call 911 ...", the same sentences
//   after; the answers are recorded as the proxy's account under the grant, and the clinician sees them as a proxy's.
// Creator: Tahseen Arnan
public class TC_UC22_02_AlternativeFlow(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    const string Help = "call 911 or go to the nearest emergency room now. You can call or text 988 at any time, day or night, to talk with someone at "
        + "the 988 Suicide & Crisis Lifeline. Your care team will read your answers before your visit, but not as soon as you send them.";

    [Fact]
    public async Task An_answer_about_self_harm_leads_with_where_to_get_help_and_a_proxy_answers_as_themselves()
    {
        var visit = await VisitAsync(apps, "Dr. Forms Two");
        var later = await BookAsync(apps, visit.Session, visit.Patient, visit.Clinician.Id, days: 3, time: "16:00");
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
        Assert.Contains("You are answering for Synthetic Booked. The questions ask how they have been.", Text(await proxy.Client.GetStringAsync(FormPath(second), Cancel)));
        using var byProxy = await SendAsync(proxy.Client, second, Answers(PhqItems, 0, 0, 0, 0, 0, 0, 0, 0, 1));
        Assert.Contains("If Synthetic Booked is thinking about hurting themselves, or you think they might act on these thoughts, " + Help, Text(await ReadAsync(byProxy)));
        Assert.Equal([$"{proxy.Account} {grant}"], await QueryAsync(apps,
            "SELECT answered_by || ' ' || proxy_grant_id FROM ehr.questionnaire_responses WHERE request_id = @request", ("request", second)));
        Assert.Contains("(proxy), not the patient", Text(await visit.Session.Client.GetStringAsync(FormsPath(visit.Patient) + "?sensitive=true", Cancel)));
    }
}
