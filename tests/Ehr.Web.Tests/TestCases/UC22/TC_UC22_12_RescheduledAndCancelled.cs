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
// Type: Functional (alternative flow: the appointment changes)
// Requirements: proposed FR-59, FR-17
// Use case: UC 22, Answer a pre-visit form; UC 16, Schedule an appointment
// Preconditions: The clinician has the patient on their panel and an appointment two days ahead with a GAD-7 and a
//   PHQ-9 asked for before it. The patient is signed in to the portal.
// Inputs and system conditions: POST /appointments/{id}?handler=Reschedule and ?handler=Cancel as the clinician; GET
//   /portal/forms and /portal/forms/{form}, POST /portal/forms/{form} as the patient; GET /patients/{id}/forms.
// Test data: Clinician "Dr. Forms Twelve". Rescheduled to twelve days ahead, then to one day ahead, at 16:00 UTC.
// Steps:
//   1. Reschedule to twelve days ahead; as the patient, open and send the GAD-7.
//   2. Reschedule to one day ahead; send the GAD-7.
//   3. Cancel the appointment; open the portal's list and the PHQ-9; open the clinician's forms page.
// Expected result: Step 1: the GAD-7 says when it can be filled in, from five days ahead; sending it is 404, nothing
//   stored. Step 2: stored. Step 3: the portal lists the GAD-7 as sent and not the PHQ-9, whose page says it can no
//   longer be filled in; the clinician sees both under the cancelled appointment, the PHQ-9 "Not answered. Closed.".
// Creator: Tahseen Arnan
public class TC_UC22_12_RescheduledAndCancelled(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    static string Day(int days) => DateTime.UtcNow.Date.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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
                ("Day", Day(days)), ("Time", "16:00"), ("Minutes", 20), ("Reason", "Synthetic: follow-up"));
            Assert.Contains("Appointment rescheduled:", Words(await ReadAsync(moved)));
        }

        await RescheduleAsync(12);
        Assert.Contains($"You can fill it in from {DateTime.UtcNow.Date.AddDays(5):MMM d, yyyy}, 16:00 UTC", Text(await portal.GetStringAsync(FormPath(gad), Cancel)));
        var answers = Answers(GadItems, 0, 0, 0, 0, 0, 0, 0);
        using (var early = await SendAsync(portal, gad, answers))
        {
            Assert.Equal(HttpStatusCode.NotFound, early.StatusCode);
        }

        Assert.Empty(await QueryAsync(apps, "SELECT id::text FROM ehr.questionnaire_responses WHERE request_id = @request", ("request", gad)));

        await RescheduleAsync(1);
        using (var sent = await SendAsync(portal, gad, answers))
        {
            Assert.Contains("Answers sent:", Text(await ReadAsync(sent)));
        }

        using (var cancelled = await AppointmentTests.ChangeAsync(visit.Session.Client, visit.Appointment, "Cancel", ("CancelReason", "Synthetic: unwell")))
        {
            Assert.Contains("Appointment cancelled:", Words(await ReadAsync(cancelled)));
        }

        var list = await portal.GetStringAsync("/portal/forms", Cancel);
        Assert.Contains("data-form=\"sent\"", list);
        Assert.DoesNotContain("data-form=\"open\"", list);
        Assert.Contains("This form can no longer be filled in.", Text(await portal.GetStringAsync(FormPath(phq), Cancel)));
        var clinician = Text(await visit.Session.Client.GetStringAsync(FormsPath(visit.Patient) + "?sensitive=true", Cancel));
        Assert.Contains("Cancelled", clinician);
        Assert.Contains("Score 0 of 21 · Below mild", clinician);
        Assert.Contains("Not answered. Closed.", clinician);
    }
}
