using System.Net;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.AppointmentTests;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC16;

// Test Case ID: TC-UC16-01
// Purpose / scenario: A clinician books an appointment from a patient's chart. The chart lists it, the clinician's
//   day shows it, and when the patient arrives the clinician checks them in, which starts their visit.
// Type: Functional (successful flow)
// Requirements: FR-17, proposed FR-42
// Use case: UC 16, Schedule an appointment
// Preconditions: The clinician is signed in on the on-site app and the patient is on their panel, with no appointments.
// Inputs and system conditions: GET /patients/{id}; GET and POST /patients/{id}/appointments/new; GET
//   /schedule?day=...; GET /appointments/{id}; POST /appointments/{id}?handler=CheckIn; on the on-site app.
// Test data: Clinician "Dr. Schedule One". Patient Synthetic Booked. A day 30 days ahead, 09:30 UTC, 20 minutes,
//   reason "Synthetic: blood pressure check".
// Steps:
//   1. Open the chart and follow "Book appointment".
//   2. Enter the day, time and reason, and choose "Book appointment".
//   3. Open the schedule for that day.
//   4. Open the appointment and choose "Check in".
// Expected result: Step 1: the form shows the patient's banner, with the signed-in clinician chosen, and is one
//   recorded read. Step 2: the chart lists the appointment under "Appointments" with its time, length, clinician and
//   reason; the row's booker is the clinician and the write audit names them. Step 3: the day shows 09:30 to 09:50
//   UTC, the patient linked to their chart, the reason and "Booked". Step 4: the page says "Checked in:"; the
//   appointment is fulfilled and names a visit of the patient, in progress, with the appointment's reason; the chart's
//   recent visits list it; the appointment offers no more changes.
// Creator: Tahseen Arnan
public class TC_UC16_01_SuccessfulFlow(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_clinician_books_an_appointment_sees_it_on_their_day_and_checks_the_patient_in()
    {
        var (clinician, patient, session) = await ClinicianAsync(apps, "Dr. Schedule One");
        var chart = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.Matches($"<a [^>]*data-book-appointment[^>]*href=\"{NewPath(patient)}\"[^>]*>Book appointment</a>", chart);
        Assert.Contains("No upcoming appointments.", chart);

        var reads = await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient);
        var form = await session.Client.GetStringAsync(NewPath(patient), Cancel);
        Assert.Contains("Book appointment: Booked, Synthetic", form);
        Assert.Contains($"<option value=\"{clinician.Id}\" selected=\"selected\">Dr. Schedule One</option>", form);
        Assert.Contains("The patient sees this in the portal.", form);
        Assert.Equal(reads + 1, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient));

        using var booked = await BookAsync(session.Client, patient, clinician.Id, Day(1), "09:30", "Synthetic: blood pressure check");
        Assert.Equal(HttpStatusCode.OK, booked.StatusCode);
        var listed = await ReadAsync(booked);
        Assert.Contains($"<strong>{Day(1)} 09:30 UTC</strong> · 20 minutes · Dr. Schedule One", listed);
        Assert.Contains("Synthetic: blood pressure check", listed);
        Assert.Equal([$"booked {clinician.Id}"], await RowsAsync(apps, patient, "status || ' ' || booked_by_id"));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Insert, clinician.Id, patient));
        var appointment = await IdAsync(apps, patient);

        var day = await session.Client.GetStringAsync($"/schedule?day={Day(1)}", Cancel);
        Assert.Contains("09:30 to 09:50 UTC", day);
        Assert.Contains($"<a href=\"/patients/{patient}\">Booked, Synthetic</a>", day);
        Assert.Contains(">Booked</span>", day);
        Assert.Contains($"href=\"/appointments/{appointment}\"", day);

        using var checkedIn = await ChangeAsync(session.Client, appointment, "CheckIn");
        var after = await ReadAsync(checkedIn);
        Assert.Contains("Checked in: the visit has started and is on the chart.", Words(after));
        Assert.DoesNotContain("Cancel appointment</button>", after);
        Assert.Equal(["fulfilled in-progress Synthetic: blood pressure check"], await RowsAsync(apps, patient,
            "status || ' ' || (SELECT e.status || ' ' || e.reason FROM ehr.encounters e WHERE e.id = encounter_id AND e.patient_id = appointments.patient_id)"));
        Assert.Contains("Synthetic: blood pressure check", await session.Client.GetStringAsync($"/patients/{patient}", Cancel));
    }
}
