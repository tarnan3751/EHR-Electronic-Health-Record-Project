using System.Net;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.PreVisitFormTests;

namespace Ehr.Web.Tests.TestCases.UC22;

// Test Case ID: TC-UC22-01
// Purpose / scenario: A patient fills in the PHQ-9 their clinician asked for before an appointment, in the portal, in
//   one sitting, and sends it. The form says it isn't watched as it arrives and where to get help now. The patient is
//   shown the answers they gave and never a score; the clinician sees the score and its band for that visit. Nobody is
//   emailed.
// Type: Functional (successful flow)
// Requirements: proposed FR-59
// Use case: UC 22, Answer a pre-visit form
// Preconditions: The clinician has the patient on their panel, an appointment booked with them two days ahead, and has
//   asked for the PHQ-9 before it. The patient has a confirmed portal account.
// Inputs and system conditions: GET /portal; GET /portal/forms; GET and POST /portal/forms/{form}; on the on-site app.
// Test data: Clinician "Dr. Forms One". Answers: "Several days" to the first seven questions, "Not at all" to the last
//   two, and "Somewhat difficult" to the difficulty question: a total of 7.
// Steps:
//   1. Sign in to the portal and follow "Pre-visit forms (1 to fill in)".
//   2. Follow "Fill in the form".
//   3. Choose the answers and "Send to my care team".
//   4. As the clinician, open the patient's pre-visit forms with sensitive items shown.
// Expected result: Step 1: the forms page says "Not monitored." and the sentence with 911 and 988, and lists the PHQ-9
//   before the appointment, to fill in by its end; one recorded view of sensitive items. Step 2: the page's title names
//   no questionnaire; the prompt and the ten questions in the form's words, each a group of its answers; the authors'
//   attribution line. Step 3: the page says "Sent. Your care team will go over your answers with you at your visit."
//   and lists each answer given, with no score, band or "of 27"; the answers are stored with the database's score of 7,
//   band Mild, no follow-up and no alert, as the patient's own; the home page still links to "Pre-visit forms", with
//   nothing to fill in; no email; neither the home page, the forms page nor the form shows the number 7 or a score
//   element. Step 4: "Score 7 of 27 · Mild", answered by the patient.
// Creator: Tahseen Arnan
public class TC_UC22_01_SuccessfulFlow(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_patient_fills_in_and_sends_a_phq_9_and_the_clinician_sees_its_score_for_that_visit()
    {
        var visit = await VisitAsync(apps, "Dr. Forms One");
        using (var asked = await AskAsync(visit.Session.Client, visit.Patient, visit.Appointment, Phq9))
        {
            Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
        }

        var request = await RequestAsync(apps, visit.Appointment, Phq9);
        var emails = apps.Emails.Sent.Count;
        var (portal, _) = await PortalAsync(apps, visit);

        var home = await portal.GetStringAsync("/portal", Cancel);
        Assert.Contains("Pre-visit forms (1 to fill in)", Text(home));
        var sensitiveReads = await apps.AuditEventsAsync(AuditActions.ReadSensitive, visit.Account, visit.Patient);
        var list = Text(await portal.GetStringAsync("/portal/forms", Cancel));
        Assert.Contains("Not monitored. Your care team reads this form before your visit, not when you send it. If you need help now, call 911. "
            + "You can call or text 988 at any time to talk with someone at the 988 Suicide & Crisis Lifeline.", list);
        Assert.Contains("Patient Health Questionnaire-9 (PHQ-9) Before your appointment on", list);
        Assert.Contains("with Dr. Forms One. Fill it in by", list);
        Assert.Equal(sensitiveReads + 1, await apps.AuditEventsAsync(AuditActions.ReadSensitive, visit.Account, visit.Patient));

        var formHtml = await portal.GetStringAsync(FormPath(request), Cancel);
        Assert.Contains("<title>Pre-visit form", formHtml);
        var form = Text(formHtml);
        Assert.Contains("Over the last 2 weeks, how often have you been bothered by any of the following problems?", form);
        Assert.Equal(10, System.Text.RegularExpressions.Regex.Matches(formHtml, "<fieldset class=\"sc-choice\"").Count);
        Assert.Contains("Feeling bad about yourself — or that you are a failure or have let yourself or your family down Not at all Several days More than half the days Nearly every day", form);
        Assert.Contains("Developed by Drs. Robert L. Spitzer, Janet B.W. Williams, Kurt Kroenke and colleagues, with an educational grant from Pfizer Inc. No permission required to reproduce, translate, display or distribute.", form);

        var answers = Answers(PhqItems, 1, 1, 1, 1, 1, 1, 1, 0, 0);
        answers["69722-7"] = "LA6573-5";
        using var sent = await SendAsync(portal, request, answers);
        Assert.Equal(FormPath(request), PathOf(sent));
        var shown = Text(await ReadAsync(sent));
        Assert.Contains("Sent. Your care team will go over your answers with you at your visit.", shown);
        Assert.Contains("Little interest or pleasure in doing things Several days", shown);
        Assert.Contains("Somewhat difficult", shown);
        foreach (var never in new[] { "Score", "score", "Mild", "of 27", "911 or go to the nearest emergency room" })
        {
            Assert.DoesNotContain(never, shown, StringComparison.Ordinal);
        }

        Assert.Equal([$"7 Mild false false {visit.Account} none"], await QueryAsync(apps,
            "SELECT score || ' ' || band || ' ' || follow_up || ' ' || alert || ' ' || answered_by || ' ' || coalesce(proxy_grant_id::text, 'none') FROM ehr.questionnaire_responses WHERE request_id = @request",
            ("request", request)));
        Assert.Equal(emails, apps.Emails.Sent.Count);

        // The link stays, with nothing to fill in, and no portal page shows the score, however it might be worded.
        var after = await portal.GetStringAsync("/portal", Cancel);
        Assert.Contains("Pre-visit forms", Text(after));
        Assert.DoesNotContain("to fill in", Text(after));
        foreach (var page in new[] { after, await portal.GetStringAsync("/portal/forms", Cancel), await portal.GetStringAsync(FormPath(request), Cancel) })
        {
            ShowsNoScore(page, 7);
        }

        var clinician = Text(await visit.Session.Client.GetStringAsync(FormsPath(visit.Patient) + "?sensitive=true", Cancel));
        Assert.Contains("Score 7 of 27 · Mild", clinician);
        Assert.Contains("by the patient", clinician);
    }
}
