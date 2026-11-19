using System.Globalization;
using System.Net;
using Xunit;
using static Ehr.Web.Tests.AppointmentTests;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC16;

// Test Case ID: TC-UC16-07
// Purpose / scenario: With the clinic's time zone set, an appointment's day and time are the clinic's: it is booked
//   on the clinic's clock, listed on the clinic's calendar day, and shown with the zone's abbreviation. A time the
//   clocks skip is refused.
// Type: Functional (alternative flow: the clinic's time zone)
// Requirements: FR-17, proposed FR-52
// Use case: UC 16, Schedule an appointment
// Preconditions: The on-site app is configured with Ehr:Clinic:TimeZone America/Chicago and the abbreviations
//   "CST,CDT". The clinician is signed in with the patient on their panel.
// Inputs and system conditions: POST /patients/{id}/appointments/new; GET /schedule?day=...; GET /appointments/{id}.
// Test data: An appointment at 23:30 on a day about a month ahead, 20 minutes, "Synthetic: late clinic"; and one at
//   02:30 on 14 March 2027, when Chicago's clocks go from 02:00 to 03:00.
// Steps:
//   1. Open the booking form and book the 23:30 appointment.
//   2. Open the schedule for that day, and for the next day.
//   3. Open the appointment.
//   4. Open the next day's schedule on an app whose clinic is in UTC.
//   5. Try to book 02:30 on 14 March 2027.
// Expected result: Step 1: the time field is labeled with the clinic's zone; the chart lists the appointment at
//   23:30 with the zone's abbreviation; it is stored at that moment, which in UTC is early the next day. Step 2: the
//   day's schedule lists it as "23:30 to 23:50" with the abbreviation and says times are clinic time; the next day's
//   doesn't list it. Step 3: the page shows the clinic's day and time, and the form to reschedule holds them. Step 4:
//   the UTC clinic lists the same appointment on the next day at its UTC time. Step 5: 422, "That time doesn't exist
//   on that day: the clocks go forward. Choose another.", and nothing is booked.
// Creator: Tahseen Arnan
public class TC_UC16_07_ScheduleInClinicTime(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_appointment_is_booked_listed_and_shown_on_the_clinics_clock()
    {
        var clock = ClinicTimeTests.ChicagoClock;
        using var chicago = ClinicTimeTests.Chicago(apps);
        var (clinician, patient, utcSession) = await ClinicianAsync(apps, "Dr. Schedule Central");
        var session = await SignInAsync(chicago, clinician);
        var day = DateOnly.ParseExact(Day(7), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var start = clock.From(day.ToDateTime(new TimeOnly(23, 30)))!.Value;
        var next = day.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var label = clock.Label(start);

        var form = await session.Client.GetStringAsync(NewPath(patient), Cancel);
        Assert.Matches("Time, clinic time, C[SD]T <span>\\(required\\)</span>", form);
        using var booked = await BookAsync(session.Client, patient, clinician.Id, Day(7), "23:30", "Synthetic: late clinic");
        Assert.Equal(HttpStatusCode.OK, booked.StatusCode);
        Assert.Contains($"<strong>{Day(7)} 23:30 {label}</strong> · 20 minutes · Dr. Schedule Central", await ReadAsync(booked));
        // The same moment, stored in UTC: early the next day.
        Assert.Equal([start.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)],
            await RowsAsync(apps, patient, "to_char(starts_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI')"));
        Assert.Equal(next, start.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var listed = await session.Client.GetStringAsync($"/schedule?day={Day(7)}", Cancel);
        Assert.Contains($"23:30 to 23:50 {label}", listed);
        Assert.Contains("Times are clinic time.", listed);
        Assert.Contains($"<a href=\"/patients/{patient}\">Booked, Synthetic</a>", listed);
        Assert.DoesNotContain($"/patients/{patient}\"", await session.Client.GetStringAsync($"/schedule?day={next}", Cancel));

        var appointment = await IdAsync(apps, patient);
        var page = await session.Client.GetStringAsync($"/appointments/{appointment}", Cancel);
        Assert.Contains($"{day.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture)}, 23:30 to 23:50 {label}", page);
        Assert.Contains($"value=\"{Day(7)}\"", page);
        Assert.Contains("value=\"23:30\"", page);

        // A clinic in UTC has the same appointment on the next day, at its UTC time.
        var utc = await utcSession.Client.GetStringAsync($"/schedule?day={next}", Cancel);
        Assert.Contains($"{start.UtcDateTime:HH:mm} to {start.UtcDateTime.AddMinutes(20):HH:mm} UTC", utc);
        Assert.Contains("Times are UTC.", utc);

        // The hour the clocks skip.
        using var skipped = await BookAsync(session.Client, patient, clinician.Id, "2027-03-14", "02:30", "Synthetic: spring forward");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, skipped.StatusCode);
        Assert.Contains("That time doesn&#x27;t exist on that day: the clocks go forward. Choose another.", await skipped.Content.ReadAsStringAsync(Cancel));
        Assert.Single(await RowsAsync(apps, patient));
    }
}
