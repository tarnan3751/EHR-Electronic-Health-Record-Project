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
//   whose patient it is and how to reach the chart, while the item is open; the chart, the forms page and the patient's
//   other forms stay behind emergency access, and a clinician with no item, or whose item is done, is sent there for
//   this form too. An answer whose only open item is held by a clinician an admin then disables is told again to the
//   care team.
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
//      The covering clinician marks their item done and opens the link again; they and the clinician open the inbox's
//      Done tab.
//   6. (A second patient.) "Dr. Forms Nine Leaving" and "Dr. Forms Nine Staying" both receive an answer to review; the
//      second marks theirs done; an admin disables the first on the Staff page.
// Expected result: Step 1: the GAD-7, though it scores 21, puts nothing in any inbox; the PHQ-9 puts one item in each
//   of the two clinicians' inboxes and no one else's, and no email is sent. Step 2: the item is first, before the older
//   one, as "Pre-visit form to review", "A pre-visit form has an answer that needs review", tagged "Needs review",
//   linking to the answers. Step 3: both charts say "Pre-visit form to review."
//   Step 4: the clinician's chart no longer says so; the colleague's still does. Step 5 (after the colleague marks theirs
//   done too): the covering clinician's inbox holds the item; its link shows the answers with "Not one of your
//   patients. Synthetic Booked is a patient of Dr. Forms Colleague, Dr. Forms Nine. To open their chart, break the
//   glass." and "Needs review. Question 9, "Thoughts that you would be better off dead or of
//   hurting yourself in some way"."; one recorded view of sensitive items and no emergency access; the chart, the
//   forms page and the GAD-7's answers each send them to the chart's reason page; so does the link, for the bystander;
//   the inbox says beside "Mark done": "Once done, the answers open only from the chart."; once it is done, the link sends them to the reason page too, and
//   their Done tab keeps the item's line with no link to the answers and nothing more; the clinician's Done tab, the
//   patient on their panel, says "Open from the patient's chart." as plain words, with no link to the answers. Step
//   6: the staying clinician's inbox links to the answers again, with a new open item beside the done one; the
//   disabled clinician keeps theirs.
// Creator: Tahseen Arnan
public class TC_UC22_09_AnswerToReview(WebApps apps) : IClassFixture<WebApps>
{
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

        const string Notice = "Pre-visit form to review. Open pre-visit forms";
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
            Assert.Contains("Not one of your patients. Synthetic Booked is a patient of Dr. Forms Colleague, Dr. Forms Nine. To open their chart, "
                + "break the glass.", page);
            Assert.Contains("Needs review. Question 9, \"Thoughts that you would be better off dead or of hurting yourself in some way\".", page);
            Assert.Contains("Thoughts that you would be better off dead or of hurting yourself in some way Several days · score 1", page);
            Assert.Contains("Back to inbox", page);
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

        // The inbox says, beside "Mark done", that the item is the way to the answers only while it is open; once the
        // covering clinician marks it done, they are sent to the chart's reason page like anyone else.
        Assert.Contains("Mark done Once done, the answers open only from the chart.",
            Text(await covering.Client.GetStringAsync("/inbox", Cancel)));
        var coveringItem = (await QueryAsync(apps, "SELECT id::text FROM ehr.inbox_items WHERE recipient_id = @covering AND kind = 'form-alert'",
            ("covering", coveringAccount.Id))).Single();
        using (var coveringDone = await PostFormAsync(covering.Client, $"/inbox?handler=Done&id={coveringItem}", "/inbox"))
        {
            Assert.Equal(HttpStatusCode.OK, coveringDone.StatusCode);
        }

        using (var closed = await covering.Client.GetAsync(link, Cancel))
        {
            Assert.Equal($"/patients/{visit.Patient}/reason", PathOf(closed));
        }

        // The Done tab keeps the item's line but no link to the answers: off the panel it says nothing more; on it, that
        // the answers open from the chart, as plain words.
        const string FromChart = "Open from the patient's chart.";
        var coveringDoneTab = await covering.Client.GetStringAsync("/inbox?done=true", Cancel);
        Assert.Contains("Pre-visit form to review A pre-visit form has an answer that needs review", Text(coveringDoneTab));
        Assert.DoesNotContain($"href=\"{link}\"", coveringDoneTab);
        Assert.DoesNotContain(FromChart, Text(coveringDoneTab));
        var panelDoneTab = await visit.Session.Client.GetStringAsync("/inbox?done=true", Cancel);
        Assert.DoesNotContain($"href=\"{link}\"", panelDoneTab);
        // The note sits in the item's own row, where its link was.
        var formRow = Text(System.Text.RegularExpressions.Regex.Match(panelDoneTab, "<tr data-inbox-item=\"form-alert\">.*?</tr>", System.Text.RegularExpressions.RegexOptions.Singleline).Value);
        Assert.Contains("A pre-visit form has an answer that needs review Needs review", formRow);
        Assert.Contains(FromChart, formRow);
        Assert.DoesNotMatch("<a[^>]*>Open from the patient", panelDoneTab);
    }

    // An admin disables, on the Staff page, the one clinician still holding an answer's item open: the answer is told
    // again at once to the care team, here the colleague on the panel who had marked theirs done.
    [Fact]
    public async Task An_answer_held_only_by_a_clinician_an_admin_disables_is_told_again_to_the_care_team()
    {
        var visit = await VisitAsync(apps, "Dr. Forms Nine Leaving");
        var colleagueAccount = await apps.CreateStaffAsync("Dr. Forms Nine Staying", StaffRoles.Clinician);
        await apps.AssignAsync(colleagueAccount, visit.Patient);
        using (var asked = await AskAsync(visit.Session.Client, visit.Patient, visit.Appointment, Phq9))
        {
            Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
        }

        var answers = await AnswerAsync(apps, visit.Account, await RequestAsync(apps, visit.Appointment, Phq9), Answers(PhqItems, 0, 0, 0, 0, 0, 0, 0, 0, 1));
        var colleague = await SignInAsync(apps.OnPrem, colleagueAccount);
        var theirs = (await QueryAsync(apps, "SELECT id::text FROM ehr.inbox_items WHERE recipient_id = @colleague AND kind = 'form-alert'",
            ("colleague", colleagueAccount.Id))).Single();
        using (var done = await PostFormAsync(colleague.Client, $"/inbox?handler=Done&id={theirs}", "/inbox"))
        {
            Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        }

        Assert.DoesNotContain($"/forms/{answers}", await colleague.Client.GetStringAsync("/inbox", Cancel));

        var admin = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Forms Admin", StaffRoles.Admin));
        using (var disabled = await PostFormAsync(admin.Client, "/admin/staff?handler=Disable", "/admin/staff", ("id", visit.Clinician.Id)))
        {
            Assert.Contains("is disabled.", await ReadAsync(disabled));
        }

        Assert.Contains($"href=\"/patients/{visit.Patient}/forms/{answers}\"", await colleague.Client.GetStringAsync("/inbox", Cancel));
        Assert.Equal(new[] { $"{colleagueAccount.Id} true", $"{colleagueAccount.Id} false", $"{visit.Clinician.Id} true" }.Order(StringComparer.Ordinal),
            (await QueryAsync(apps, "SELECT recipient_id || ' ' || (done_at IS NULL) FROM ehr.inbox_items WHERE source_id = @answers", ("answers", answers)))
            .Order(StringComparer.Ordinal));
    }
}
