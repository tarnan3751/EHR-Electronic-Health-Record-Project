using System.Net;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.PreVisitFormTests;

namespace Ehr.Web.Tests.TestCases.UC22;

// Test Case ID: TC-UC22-09
// Purpose / scenario: A PHQ-9 whose question 9 ("Thoughts that you would be better off dead or of hurting yourself in
//   some way") is answered other than "Not at all" reaches the care team at once, with no email: an item in the inbox
//   of the appointment's clinician and of each clinician with the patient on their panel, before every other item,
//   marked "Needs review", naming no questionnaire, answer or score, linking to the answers; and a notice on the chart
//   for each of them until they mark their own item done. A form with no such answer puts nothing in anyone's inbox.
//   A clinician given the item who doesn't have the patient on their panel (here, one who joined the panel when nobody
//   on the team still held it open, then left) reads that form's answers from the inbox without emergency access, told
//   whose patient it is and how to reach the chart; the chart, the forms page and the patient's other forms stay behind
//   emergency access, and a clinician with no item is sent there for this form too.
// Type: Functional (exceptional condition: an answer that needs review)
// Requirements: proposed FR-59, FR-41
// Use case: UC 22, Answer a pre-visit form; UC 15, Work through the inbox
// Preconditions: The clinician is signed in on the on-site app, the patient is on their panel and a colleague's, with an
//   appointment booked with the clinician two days ahead and a PHQ-9 and a GAD-7 asked for before it; the clinician's
//   inbox already holds an older item.
// Inputs and system conditions: answers sent as the patient's portal account; GET /inbox; GET /patients/{id}; POST
//   /inbox?handler=Done; on the on-site app.
// Test data: Clinician "Dr. Forms Nine" and colleague "Dr. Forms Colleague". The GAD-7 answered "Nearly every day" to
//   every question; the PHQ-9 answered "Several days" to question 9 and "Not at all" to the rest.
// Steps:
//   1. Send the GAD-7's answers, then the PHQ-9's.
//   2. Open the inbox, as the clinician.
//   3. Open the chart, as each of the two.
//   4. Mark the clinician's item done, and open the chart again.
//   5. A third clinician, "Dr. Forms Covering", joins the patient's panel and leaves it; they open the inbox's link,
//      then the chart, the forms page and the GAD-7's answers. "Dr. Forms Bystander", with no item, opens the link.
// Expected result: Step 1: the GAD-7, though it scores 21, puts nothing in any inbox; the PHQ-9 puts one item in each
//   of the two clinicians' inboxes and no one else's, and no email is sent. Step 2: the item is first, before the older
//   one, as "Pre-visit form to review", "A pre-visit form has an answer that needs review", tagged "Needs review",
//   linking to the answers. Step 3: both charts say "Needs review: a pre-visit form has an answer for you to review."
//   Step 4: the clinician's chart no longer says so; the colleague's still does. Step 5 (after the colleague marks theirs
//   done too): the covering clinician's inbox holds the item; its link shows the answers with "Not on your panel:
//   Synthetic Booked is on the panel of Dr. Forms Colleague, Dr. Forms Nine, and the chart opens to you only under emergency access, which
//   the chart asks you for." and "An answer needs review: question 9, "Thoughts that you would be better off dead or of
//   hurting yourself in some way"."; one recorded view of sensitive items and no emergency access; the chart, the
//   forms page and the GAD-7's answers each send them to the chart's reason page; so does the link, for the bystander.
// Creator: Tahseen Arnan
public class TC_UC22_09_AnswerToReview(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_answer_about_self_harm_reaches_the_care_teams_inboxes_first_and_their_charts_with_no_email()
    {
        var visit = await VisitAsync(apps, "Dr. Forms Nine");
        var colleagueAccount = await apps.CreateStaffAsync("Dr. Forms Colleague", StaffRoles.Clinician);
        await apps.AssignAsync(colleagueAccount, visit.Patient);
        var colleague = await SignInAsync(apps.OnPrem, colleagueAccount);
        foreach (var code in new[] { Phq9, Gad7 })
        {
            using var asked = await AskAsync(visit.Session.Client, visit.Patient, visit.Appointment, code);
        }

        await QueryAsync(apps,
            """
            INSERT INTO ehr.inbox_items (id, recipient_id, kind, patient_id, summary, created_at)
            VALUES (gen_random_uuid(), @clinician, 'addendum', @patient, 'An addendum was added to a note you signed', now() - interval '1 day')
            """, ("clinician", visit.Clinician.Id), ("patient", visit.Patient));
        var emails = apps.Emails.Sent.Count;

        await AnswerAsync(apps, visit.Account, await RequestAsync(apps, visit.Appointment, Gad7), Answers(GadItems, 3, 3, 3, 3, 3, 3, 3));
        Assert.Empty(await QueryAsync(apps, "SELECT id::text FROM ehr.inbox_items WHERE patient_id = @patient AND kind = 'form-alert'", ("patient", visit.Patient)));
        var answers = await AnswerAsync(apps, visit.Account, await RequestAsync(apps, visit.Appointment, Phq9), Answers(PhqItems, 0, 0, 0, 0, 0, 0, 0, 0, 1));
        Assert.Equal(new[] { visit.Clinician.Id, colleagueAccount.Id }.Select(id => id.ToString()).Order(StringComparer.Ordinal),
            await QueryAsync(apps, "SELECT recipient_id::text FROM ehr.inbox_items WHERE patient_id = @patient AND kind = 'form-alert' ORDER BY 1", ("patient", visit.Patient)));
        Assert.Equal(emails, apps.Emails.Sent.Count);

        var inbox = await visit.Session.Client.GetStringAsync("/inbox", Cancel);
        var rows = System.Text.RegularExpressions.Regex.Matches(inbox, "data-inbox-item=\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToList();
        Assert.Equal(["form-alert", "addendum"], rows);
        Assert.Contains("Pre-visit form to review A pre-visit form has an answer that needs review Needs review", Text(inbox));
        Assert.Contains($"href=\"/patients/{visit.Patient}/forms/{answers}\"", inbox);
        foreach (var never in new[] { "PHQ", "GAD", "Several days", "score", "Score" })
        {
            Assert.DoesNotContain(never, Text(inbox), StringComparison.Ordinal);
        }

        const string Notice = "Needs review: a pre-visit form has an answer for you to review.";
        Assert.Contains(Notice, Text(await visit.Session.Client.GetStringAsync($"/patients/{visit.Patient}", Cancel)));
        Assert.Contains(Notice, Text(await colleague.Client.GetStringAsync($"/patients/{visit.Patient}", Cancel)));

        var item = (await QueryAsync(apps, "SELECT id::text FROM ehr.inbox_items WHERE recipient_id = @clinician AND kind = 'form-alert'",
            ("clinician", visit.Clinician.Id))).Single();
        using var done = await PostFormAsync(visit.Session.Client, $"/inbox?handler=Done&id={item}", "/inbox");
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.DoesNotContain(Notice, Text(await visit.Session.Client.GetStringAsync($"/patients/{visit.Patient}", Cancel)));
        Assert.Contains(Notice, Text(await colleague.Client.GetStringAsync($"/patients/{visit.Patient}", Cancel)));

        // Nobody on the team holds it open any more; a clinician who joins the panel is given it, and keeps it on leaving.
        var theirs = (await QueryAsync(apps, "SELECT id::text FROM ehr.inbox_items WHERE recipient_id = @colleague AND kind = 'form-alert'",
            ("colleague", colleagueAccount.Id))).Single();
        using (var colleagueDone = await PostFormAsync(colleague.Client, $"/inbox?handler=Done&id={theirs}", "/inbox"))
        {
            Assert.Equal(HttpStatusCode.OK, colleagueDone.StatusCode);
        }

        var coveringAccount = await apps.CreateStaffAsync("Dr. Forms Covering", StaffRoles.Clinician);
        await apps.AssignAsync(coveringAccount, visit.Patient);
        await QueryAsync(apps, "UPDATE ehr.patient_assignments SET ended_at = now() WHERE clinician_id = @covering AND ended_at IS NULL", ("covering", coveringAccount.Id));
        var covering = await SignInAsync(apps.OnPrem, coveringAccount);
        var link = $"/patients/{visit.Patient}/forms/{answers}";
        Assert.Contains($"href=\"{link}\"", await covering.Client.GetStringAsync("/inbox", Cancel));

        var views = await apps.AuditEventsAsync(AuditActions.ReadSensitive, coveringAccount.Id, visit.Patient);
        var emergencies = await apps.AuditEventsAsync(AuditActions.BreakGlass, coveringAccount.Id);
        using (var read = await covering.Client.GetAsync(link, Cancel))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            var page = Text(await read.Content.ReadAsStringAsync(Cancel));
            Assert.Contains("Not on your panel: Synthetic Booked is on the panel of Dr. Forms Colleague, Dr. Forms Nine, and the chart opens to you only "
                + "under emergency access, which the chart asks you for.", page);
            Assert.Contains("An answer needs review: question 9, \"Thoughts that you would be better off dead or of hurting yourself in some way\".", page);
            Assert.Contains("Thoughts that you would be better off dead or of hurting yourself in some way Several days 1", page);
            Assert.Contains("Back to the inbox", page);
        }

        Assert.Equal(views + 1, await apps.AuditEventsAsync(AuditActions.ReadSensitive, coveringAccount.Id, visit.Patient));
        Assert.Equal(emergencies, await apps.AuditEventsAsync(AuditActions.BreakGlass, coveringAccount.Id));
        Assert.Equal(["0"], await QueryAsync(apps, "SELECT count(*)::text FROM ehr.break_glass_accesses WHERE clinician_id = @covering", ("covering", coveringAccount.Id)));

        // Nothing else of the chart: each is sent to the chart's reason page, as before.
        var gad = (await QueryAsync(apps, "SELECT id::text FROM ehr.questionnaire_responses WHERE patient_id = @patient AND questionnaire_code = @code",
            ("patient", visit.Patient), ("code", Gad7))).Single();
        foreach (var elsewhere in new[] { $"/patients/{visit.Patient}", FormsPath(visit.Patient) + "?sensitive=true", $"{FormsPath(visit.Patient)}/{gad}" })
        {
            using var refused = await covering.Client.GetAsync(elsewhere, Cancel);
            Assert.Equal($"/patients/{visit.Patient}/reason", PathOf(refused));
        }

        // A clinician with no item is sent there for this form too.
        var bystander = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Dr. Forms Bystander", StaffRoles.Clinician));
        using (var gated = await bystander.Client.GetAsync(link, Cancel))
        {
            Assert.Equal($"/patients/{visit.Patient}/reason", PathOf(gated));
        }
    }
}
