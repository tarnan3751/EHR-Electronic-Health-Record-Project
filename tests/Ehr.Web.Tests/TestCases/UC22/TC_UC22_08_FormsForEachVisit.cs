using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.PreVisitFormTests;

namespace Ehr.Web.Tests.TestCases.UC22;

// Test Case ID: TC-UC22-08
// Purpose / scenario: The clinician reads a patient's pre-visit forms, for each visit: hidden until asked, as sensitive
//   items are; then each form's score out of its most, its severity band, whether it needs follow-up, who answered,
//   and a trend of each questionnaire's scores; and one form's answers, question by question, in the wording it was
//   asked with. A proxy's answers say they are a proxy's. Another patient's forms never appear among them.
// Type: Functional (successful flow: the clinician's side)
// Requirements: proposed FR-59, FR-39
// Use case: UC 22, Answer a pre-visit form
// Preconditions: The clinician is signed in on the on-site app; the patient is on their panel with a portal account and
//   two appointments booked with the clinician, two and three days ahead; a PHQ-9 and a GAD-7 asked for before each.
//   Another patient, of another clinician, has a PHQ-9 answered "Nearly every day" throughout (27, Severe).
// Inputs and system conditions: answers sent as the patient's portal account (the PHQ-9's for the first appointment by
//   a proxy the patient allowed sensitive items); GET /patients/{id}/forms, with and without sensitive=true; GET
//   /patients/{id}/forms/{answers}; on the on-site app.
// Test data: Clinician "Dr. Forms Eight". PHQ-9 totals 12 (first appointment, by the proxy) and 3; GAD-7 totals 4 and
//   10. The proxy's account is named "Synthetic Proxy".
// Steps:
//   1. Open the forms page.
//   2. Choose "Show sensitive items".
//   3. Open the answers of the first PHQ-9, and the other patient's answers at this patient's address.
// Expected result: Step 1: "Sensitive items hidden. 4 pre-visit forms are not shown." and no form's
//   score or band; one recorded read of the patient. Step 2: "Sensitive items shown"; one recorded view of sensitive
//   items; each appointment's forms under it: "Score 12 of 27 · Moderate" with "Needs follow-up" and "Answered ... by
//   Synthetic Proxy (proxy), not the patient", "Score 3 of 27 · Minimal", "Score 4 of 21 · Below mild", "Score 10 of 21 ·
//   Moderate" with "Needs follow-up"; a trend for each questionnaire, saying it needs follow-up at or above 10; and the
//   note that "Below mild" is the clinic's wording; nothing of the other patient's form. Step 3: each of the PHQ-9's
//   questions in the form's words with the answer chosen and its score, the difficulty question "Not answered", the
//   total, and the authors' attribution line; one recorded view of sensitive items; the other patient's answers are 404
//   there. The desktop app's offline copy holds none of the forms and counts the four
//   among the sensitive items it doesn't keep.
// Creator: Tahseen Arnan
public class TC_UC22_08_FormsForEachVisit(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task The_clinician_reads_each_visits_forms_scored_banded_and_trended_once_asked()
    {
        var visit = await VisitAsync(apps, "Dr. Forms Eight");
        var later = await BookAsync(apps, visit, days: 3, time: "16:00");
        foreach (var appointment in new[] { visit.Appointment, later })
        {
            foreach (var code in new[] { Phq9, Gad7 })
            {
                using var asked = await AskAsync(visit.Session.Client, visit.Patient, appointment, code);
            }
        }

        // The proxy's grant, given by the patient with sensitive items, and their account named as a proxy-only one is.
        var proxy = await ProxyTests.PersonAsync(apps, "Synthetic", "Carer");
        await QueryAsync(apps, "UPDATE ehr.patient_users SET display_name = 'Synthetic Proxy' WHERE id = @id", ("id", proxy.Account));
        var grant = await GrantAsync(apps, visit.Account, visit.Patient, proxy.Email, sensitive: true);
        var first = await AnswerAsync(apps, proxy.Account, await RequestAsync(apps, visit.Appointment, Phq9), Answers(PhqItems, 3, 2, 2, 1, 1, 1, 1, 1, 0), grant);
        await AnswerAsync(apps, visit.Account, await RequestAsync(apps, visit.Appointment, Gad7), Answers(GadItems, 1, 1, 1, 1, 0, 0, 0));
        await AnswerAsync(apps, visit.Account, await RequestAsync(apps, later, Phq9), Answers(PhqItems, 1, 1, 1, 0, 0, 0, 0, 0, 0));
        await AnswerAsync(apps, visit.Account, await RequestAsync(apps, later, Gad7), Answers(GadItems, 2, 2, 2, 1, 1, 1, 1));

        // Another patient with forms too, whose total no form of this patient's has.
        var stranger = await VisitAsync(apps, "Dr. Forms Eight Other");
        using (var asked = await AskAsync(stranger.Session.Client, stranger.Patient, stranger.Appointment, Phq9))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, asked.StatusCode);
        }

        var theirs = await AnswerAsync(apps, stranger.Account, await RequestAsync(apps, stranger.Appointment, Phq9), Answers(PhqItems, 3, 3, 3, 3, 3, 3, 3, 3, 3));

        var reads = await apps.AuditEventsAsync(AuditActions.Read, visit.Clinician.Id, visit.Patient);
        var hiddenHtml = await visit.Session.Client.GetStringAsync(FormsPath(visit.Patient), Cancel);
        var hidden = Text(hiddenHtml);
        Assert.Contains("Sensitive items hidden. 4 pre-visit forms are not shown. Show sensitive items", hidden);
        // The questionnaires are named only as what can be asked for: no form, score, band or answerer.
        foreach (var shownOnlyWhenAsked in new[] { "Score", "Moderate", "Minimal", "Below mild", "proxy", "Answered" })
        {
            Assert.DoesNotContain(shownOnlyWhenAsked, hidden, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("data-form-visit", hiddenHtml);

        Assert.Equal(reads + 1, await apps.AuditEventsAsync(AuditActions.Read, visit.Clinician.Id, visit.Patient));

        var sensitiveReads = await apps.AuditEventsAsync(AuditActions.ReadSensitive, visit.Clinician.Id, visit.Patient);
        var shownHtml = await visit.Session.Client.GetStringAsync(FormsPath(visit.Patient) + "?sensitive=true", Cancel);
        var shown = Text(shownHtml);
        Assert.Contains("Sensitive items shown. Your view is logged.", shown);
        Assert.Contains("Score 12 of 27 · Moderate Needs follow-up", shown);
        Assert.Contains("by Synthetic Proxy (proxy), not the patient", shown);
        Assert.Contains("Score 4 of 21 · Below mild", shown);
        Assert.Contains("Score 3 of 27 · Minimal", shown);
        Assert.Contains("Score 10 of 21 · Moderate Needs follow-up", shown);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(shownHtml, "data-form-visit").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(shownHtml, "<figure class=\"sc-trend\"").Count);
        Assert.Contains("Needs follow-up at or above 10.", shown);
        Assert.Contains("\"Below mild\" (under 5), which is this clinic's wording", shown);
        Assert.DoesNotContain("data-form-alert", shownHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("27 of 27", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("Severe", shown, StringComparison.Ordinal);
        Assert.Equal(sensitiveReads + 1, await apps.AuditEventsAsync(AuditActions.ReadSensitive, visit.Clinician.Id, visit.Patient));

        var answers = Text(await visit.Session.Client.GetStringAsync($"{FormsPath(visit.Patient)}/{first}", Cancel));
        Assert.Contains("Over the last 2 weeks, how often have you been bothered by any of the following problems?", answers);
        Assert.Contains("Little interest or pleasure in doing things Nearly every day · score 3", answers);
        Assert.Contains("Moving or speaking so slowly that other people could have noticed? Or the opposite — being so fidgety or restless that you have been moving around a lot more than usual Several days · score 1", answers);
        Assert.Contains("Thoughts that you would be better off dead or of hurting yourself in some way Not at all · score 0", answers);
        Assert.Contains("get along with other people? Not answered", answers);
        Assert.DoesNotContain("Not answered ·", answers, StringComparison.Ordinal);
        Assert.Contains("12 of 27 · Moderate Needs follow-up", answers);
        Assert.Contains("Developed by Drs. Robert L. Spitzer, Janet B.W. Williams, Kurt Kroenke and colleagues", answers);
        Assert.Equal(sensitiveReads + 2, await apps.AuditEventsAsync(AuditActions.ReadSensitive, visit.Clinician.Id, visit.Patient));
        using (var notTheirs = await visit.Session.Client.GetAsync($"{FormsPath(visit.Patient)}/{theirs}", Cancel))
        {
            Assert.Equal(System.Net.HttpStatusCode.NotFound, notTheirs.StatusCode);
        }

        // The desktop app's offline copy holds none of them, and counts them among what it doesn't keep.
        var token = await apps.DesktopSignInAsync(apps.OnPrem, visit.Clinician);
        using var offline = await DesktopSignInTests.PostAsync(apps.OnPrem, "/api/desktop/offline-panel", token);
        var copy = await offline.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("\"sensitiveNotKept\":4", copy);
        foreach (var never in new[] { "PHQ", "GAD", "Moderate", "Below mild", "LA65" })
        {
            Assert.DoesNotContain(never, copy, StringComparison.Ordinal);
        }
    }
}
