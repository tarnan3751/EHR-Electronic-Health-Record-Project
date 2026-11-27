using System.Globalization;
using System.Net;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.PreVisitFormTests;

namespace Ehr.Web.Tests.TestCases.UC22;

// Test Case ID: TC-UC22-12
// Purpose / scenario: A form follows its appointment. Rescheduled more than 7 days ahead, it isn't open until 7 days
//   before; rescheduled back, it opens again and can be sent. Cancelled, the appointment's form not yet answered closes
//   and leaves the portal's list, and the one answered stays, with its appointment, for the clinician and the patient.
//   Answers sent to a form that closed while it was being filled in are not stored, and the page says so plainly, with
//   the help an answer called for and the notice that the form isn't watched; nobody is told of them. Checked in, a
//   visit that runs past its scheduled end keeps its forms open until the visit ends, at the latest 12 hours after the
//   scheduled end; a booked one's close at the scheduled end, and the pages say when.
// Type: Functional (alternative flow: the appointment changes)
// Requirements: proposed FR-59, FR-17
// Use case: UC 22, Answer a pre-visit form; UC 16, Schedule an appointment
// Preconditions: The clinician has the patient on their panel and an appointment two days ahead with a GAD-7 and a
//   PHQ-9 asked for before it. The patient is signed in to the portal.
// Inputs and system conditions: POST /appointments/{id}?handler=Reschedule, ?handler=Cancel and ?handler=CheckIn as the
//   clinician; the visit's start moved two hours back, and the visit ended, as the superuser; GET
//   /portal/forms and /portal/forms/{form}, POST /portal/forms/{form} as the patient; GET /patients/{id}/forms.
// Test data: Clinicians "Dr. Forms Twelve" and "Dr. Forms Twelve Late". Rescheduled to twelve days ahead, then to one
//   day ahead, at 16:00 UTC. The PHQ-9 sent after the cancellation with question 9 "Several days".
// Steps:
//   1. Reschedule to twelve days ahead; as the patient, open and send the GAD-7.
//   2. Reschedule to one day ahead; open the GAD-7 and send it.
//   3. Cancel the appointment; send the PHQ-9 as it was filled in before; open the portal's list and the PHQ-9; open
//      the clinician's forms page.
//   4. For another patient, check in an appointment with both forms asked for, its start then two hours past; open the
//      portal's home and list; send the GAD-7; open the clinician's page; end the visit; send the PHQ-9.
// Expected result: Step 1: the GAD-7 says when it can be filled in, from five days ahead; sending it is 409, "Not sent:
//   your answers were not sent to your care team. This form can't be filled in yet.", nothing stored. Step 2: "Fill it in by
//   <tomorrow>, 16:20 UTC", the scheduled end; stored.
//   Step 3: sending is 409, the page leading with the help about self-harm (without saying the care team will read the
//   answers), then "Not sent: your answers were not sent to your care team. This form can no longer be filled in.",
//   and "Not monitored."; nothing stored and no inbox item; the portal lists the GAD-7 as sent and not the PHQ-9, whose
//   page says it can no longer be filled in; the clinician sees both under the cancelled appointment, the PHQ-9 "Not
//   answered. Closed.". Step 4: "Pre-visit forms (2 to fill in)", each "Fill it in by" 12 hours after the scheduled end;
//   the GAD-7 stored; the clinician sees the PHQ-9 open until that time, "while the visit is in progress."; once the visit
//   ends, sending the PHQ-9 is 409, "This form can no longer be filled in.", and the clinician sees "Not answered.
//   Closed.".
// Creator: Tahseen Arnan
public class TC_UC22_12_RescheduledAndCancelled(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_form_follows_its_appointment_through_a_reschedule_and_a_cancellation()
    {
        var visit = await VisitAsync(apps, "Dr. Forms Twelve");
        foreach (var code in new[] { Gad7, Phq9 })
        {
            using var asked = await AskAsync(visit.Session.Client, visit.Patient, visit.Appointment, code);
        }

        var gad = await RequestAsync(apps, visit.Appointment, Gad7);
        var phq = await RequestAsync(apps, visit.Appointment, Phq9);
        var (portal, _) = await PortalAsync(apps, visit);

        async Task RescheduleAsync(int days)
        {
            using var moved = await AppointmentTests.ChangeAsync(visit.Session.Client, visit.Appointment, "Reschedule",
                ("Day", Day(visit, days)), ("Time", "16:00"), ("Minutes", 20), ("Reason", "Synthetic: follow-up"));
            Assert.Contains("Appointment rescheduled.", Words(await ReadAsync(moved)));
        }

        await RescheduleAsync(12);
        Assert.Contains($"You can fill it in from {visit.Now.UtcDateTime.Date.AddDays(5):MMM d, yyyy}, 16:00 UTC", Text(await portal.GetStringAsync(FormPath(gad), Cancel)));
        var answers = Answers(GadItems, 0, 0, 0, 0, 0, 0, 0);
        using (var early = await SendAsync(portal, gad, answers))
        {
            Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
            Assert.Contains("Not sent: your answers were not sent to your care team. This form can't be filled in yet.",
                Text(await early.Content.ReadAsStringAsync(Cancel)));
        }

        Assert.Empty(await QueryAsync(apps, "SELECT id::text FROM ehr.questionnaire_responses WHERE request_id = @request", ("request", gad)));

        await RescheduleAsync(1);
        // Booked: it closes at the appointment's scheduled end, 20 minutes after its start.
        Assert.Contains($"Fill it in by {visit.Now.UtcDateTime.Date.AddDays(1):MMM d, yyyy}, 16:20 UTC, in one go",
            Text(await portal.GetStringAsync(FormPath(gad), Cancel)));
        using (var sent = await SendAsync(portal, gad, answers))
        {
            Assert.Contains("Answers sent:", Text(await ReadAsync(sent)));
        }

        using (var cancelled = await AppointmentTests.ChangeAsync(visit.Session.Client, visit.Appointment, "Cancel", ("CancelReason", "Synthetic: unwell")))
        {
            Assert.Contains("Appointment cancelled.", Words(await ReadAsync(cancelled)));
        }

        // The PHQ-9 had been filled in before the cancellation, question 9 included: not sent, and said so, with the help.
        using (var tooLate = await SendAsync(portal, phq, Answers(PhqItems, 0, 0, 0, 0, 0, 0, 0, 0, 1)))
        {
            Assert.Equal(HttpStatusCode.Conflict, tooLate.StatusCode);
            var page = Text(await tooLate.Content.ReadAsStringAsync(Cancel));
            const string Help = "If you are thinking about hurting yourself, or feel you might act on these thoughts, call 911 or go to the nearest emergency room now. "
                + "You can call or text 988 at any time, day or night, to talk with someone at the 988 Suicide & Crisis Lifeline.";
            const string NotSent = "Not sent: your answers were not sent to your care team. This form can no longer be filled in.";
            Assert.Contains(Help, page);
            Assert.Contains(NotSent, page);
            Assert.True(page.IndexOf(Help, StringComparison.Ordinal) < page.IndexOf(NotSent, StringComparison.Ordinal), "The help comes first.");
            Assert.Contains("Not monitored. Your care team reads this form before your visit", page);
            Assert.DoesNotContain("will read your answers", page, StringComparison.Ordinal);
        }

        Assert.Empty(await QueryAsync(apps, "SELECT id::text FROM ehr.questionnaire_responses WHERE request_id = @request", ("request", phq)));
        Assert.Empty(await QueryAsync(apps, "SELECT id::text FROM ehr.inbox_items WHERE patient_id = @patient AND kind = 'form-alert'", ("patient", visit.Patient)));

        var list = await portal.GetStringAsync("/portal/forms", Cancel);
        Assert.Contains("data-form=\"sent\"", list);
        Assert.DoesNotContain("data-form=\"open\"", list);
        Assert.Contains("This form can no longer be filled in.", Text(await portal.GetStringAsync(FormPath(phq), Cancel)));
        var clinician = Text(await visit.Session.Client.GetStringAsync(FormsPath(visit.Patient) + "?sensitive=true", Cancel));
        Assert.Contains("Cancelled", clinician);
        Assert.Contains("Score 0 of 21 · Below mild", clinician);
        Assert.Contains("Not answered. Closed.", clinician);
    }

    [Fact]
    public async Task A_checked_in_visit_keeps_its_forms_open_past_its_scheduled_end_until_the_visit_ends()
    {
        var visit = await VisitAsync(apps, "Dr. Forms Twelve Late");
        foreach (var code in new[] { Gad7, Phq9 })
        {
            using var asked = await AskAsync(visit.Session.Client, visit.Patient, visit.Appointment, code);
        }

        var gad = await RequestAsync(apps, visit.Appointment, Gad7);
        var phq = await RequestAsync(apps, visit.Appointment, Phq9);
        using (var checkedIn = await AppointmentTests.ChangeAsync(visit.Session.Client, visit.Appointment, "CheckIn"))
        {
            Assert.Equal(HttpStatusCode.OK, checkedIn.StatusCode);
        }

        // The visit runs late: its scheduled end is two hours past. Nothing moves a checked-in appointment, so this is the
        // superuser's, with the appointment's guard off for the statement.
        await QueryAsync(apps, "ALTER TABLE ehr.appointments DISABLE TRIGGER appointments_guard");
        await QueryAsync(apps, "UPDATE ehr.appointments SET starts_at = now() - interval '2 hours' WHERE id = @id", ("id", visit.Appointment));
        await QueryAsync(apps, "ALTER TABLE ehr.appointments ENABLE TRIGGER appointments_guard");

        // It closes 12 hours after the scheduled end, as the database's rule has it; the pages say when.
        var closes = (await QueryAsync(apps,
            "SELECT to_char((starts_at + make_interval(mins => minutes) + interval '12 hours') AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI') FROM ehr.appointments WHERE id = @id",
            ("id", visit.Appointment))).Single();
        var portalCloses = DateTime.ParseExact(closes, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture).ToString("MMM d, yyyy, HH:mm", CultureInfo.InvariantCulture);
        var (portal, _) = await PortalAsync(apps, visit);
        Assert.Contains("Pre-visit forms (2 to fill in)", Text(await portal.GetStringAsync("/portal", Cancel)));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(Text(await portal.GetStringAsync("/portal/forms", Cancel)), $"Fill it in by {portalCloses} UTC\\.").Count);
        using (var sent = await SendAsync(portal, gad, Answers(GadItems, 0, 0, 0, 0, 0, 0, 0)))
        {
            Assert.Contains("Answers sent:", Text(await ReadAsync(sent)));
        }

        Assert.Contains($"until {closes} UTC, while the visit is in progress.",
            Text(await visit.Session.Client.GetStringAsync(FormsPath(visit.Patient) + "?sensitive=true", Cancel)));

        // The visit ends: the form left closes.
        await QueryAsync(apps,
            "UPDATE ehr.encounters SET status = 'finished', period_end = now() WHERE id = (SELECT encounter_id FROM ehr.appointments WHERE id = @id)",
            ("id", visit.Appointment));
        using (var ended = await SendAsync(portal, phq, Answers(PhqItems, 0, 0, 0, 0, 0, 0, 0, 0, 0)))
        {
            Assert.Equal(HttpStatusCode.Conflict, ended.StatusCode);
            Assert.Contains("This form can no longer be filled in.", Text(await ended.Content.ReadAsStringAsync(Cancel)));
        }

        Assert.Empty(await QueryAsync(apps, "SELECT id::text FROM ehr.questionnaire_responses WHERE request_id = @request", ("request", phq)));
        Assert.Contains("Not answered. Closed.", Text(await visit.Session.Client.GetStringAsync(FormsPath(visit.Patient) + "?sensitive=true", Cancel)));
    }
}
