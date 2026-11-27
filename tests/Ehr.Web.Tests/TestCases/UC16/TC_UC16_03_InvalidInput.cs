using System.Net;
using Xunit;
using static Ehr.Web.Tests.AppointmentTests;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC16;

// Test Case ID: TC-UC16-03
// Purpose / scenario: A booking with something missing or wrong is refused with the field highlighted and what was
//   entered kept: no reason, a day that doesn't exist, a time that has passed, a clinician who isn't one. A time the
//   clinician is already booked for is refused too, and says so. A cancellation needs a reason.
// Type: Functional (invalid input)
// Requirements: FR-17, proposed FR-42
// Use case: UC 16, Schedule an appointment
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel, and has one
//   appointment booked at 09:00 for 20 minutes on a day 33 days ahead.
// Inputs and system conditions: POST /patients/{id}/appointments/new and POST /appointments/{id}?handler=Cancel on
//   the on-site app.
// Test data: Reason "" and "Synthetic: valid". Day "2027-02-30" and yesterday. Time "09:10" on the booked day. A
//   clinician ID that isn't a staff account.
// Steps:
//   1. Book with no reason; with the day that doesn't exist; with yesterday; with the unknown clinician.
//   2. Book at 09:10 on the booked day, with the same clinician.
//   3. Cancel the booked appointment with no reason.
// Expected result: Step 1: each is answered 422 with "Not saved." and, in turn, "Enter the reason for the visit.",
//   "Enter the day, as year-month-day.", "Choose a time that hasn't passed.", "Choose a clinician."; the reason typed
//   is still in its field. Step 2: answered 409 with "This clinician already has an appointment booked then. Choose
//   another time." Step 3: answered 422 with "Enter the reason for cancelling." The patient still has exactly one
//   appointment, booked.
// Creator: Tahseen Arnan
public class TC_UC16_03_InvalidInput(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_booking_that_is_incomplete_or_double_booked_is_refused_and_nothing_is_saved()
    {
        var (clinician, patient, session) = await ClinicianAsync(apps, "Dr. Schedule Three");
        using var first = await BookAsync(session.Client, patient, clinician.Id, Day(3), "09:00", "Synthetic: valid");
        var appointment = await IdAsync(apps, patient);
        var yesterday = DateTime.UtcNow.Date.AddDays(-1).ToString("yyyy-MM-dd");

        foreach (var (who, day, reason, message) in new[]
        {
            (clinician.Id, Day(3), "", "Enter the reason for the visit."),
            (clinician.Id, "2027-02-30", "Synthetic: valid", "Enter the day, as year-month-day."),
            (clinician.Id, yesterday, "Synthetic: valid", "Choose a time that hasn&#x27;t passed."),
            (Guid.NewGuid(), Day(3), "Synthetic: valid", "Choose a clinician."),
        })
        {
            using var refused = await BookAsync(session.Client, patient, who, day, "15:00", reason);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            var html = await refused.Content.ReadAsStringAsync(Cancel);
            Assert.Contains(message, html);
            Assert.Contains("Not saved.", html);
            if (reason.Length > 0)
            {
                Assert.Contains($"name=\"Reason\" value=\"{reason}\"", html);
            }
        }

        using var overlap = await BookAsync(session.Client, patient, clinician.Id, Day(3), "09:10", "Synthetic: valid");
        Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);
        var conflict = await overlap.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("This clinician already has an appointment booked then. Choose another time.", conflict);
        Assert.Matches("<input type=\"time\"[^>]*aria-invalid=\"true\" aria-describedby=\"appointment-time-error\"", conflict);

        using var noReason = await PostFormAsync(session.Client, $"/appointments/{appointment}?handler=Cancel", $"/appointments/{appointment}", ("CancelReason", " "));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
        Assert.Contains("Enter the reason for cancelling.", await noReason.Content.ReadAsStringAsync(Cancel));

        Assert.Equal(["booked"], await RowsAsync(apps, patient));
    }
}
