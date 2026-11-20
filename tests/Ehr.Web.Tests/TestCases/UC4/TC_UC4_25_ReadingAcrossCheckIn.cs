using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VitalTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-25
// Purpose / scenario: A blood pressure corrected after the patient was checked in stays one reading, with the visit
//   of its moment (step 9c, failure A): 120/80 recorded before check-in, the patient checked in five minutes later,
//   the reading marked entered in error, and 120/78 typed for the same time. The same again with no check-in.
// Type: Functional (alternative flow: correcting a blood pressure)
// Requirements: FR-08, proposed FR-50
// Use case: UC 4, View patient chart
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel. For the first patient,
//   an appointment is booked with the clinician.
// Inputs and system conditions: POST /patients/{id}/vitals; POST /appointments/{id}?handler=CheckIn; POST
//   /patients/{id}/vitals/error?at=...
// Test data: 120/80 taken five minutes ago; reason "Synthetic: diastolic misread."; then 120/78.
// Steps:
//   1. Record 120/80 taken five minutes ago.
//   2. Check the patient in for the appointment: their visit starts now, after the blood pressure was taken.
//   3. On the set's "Mark in error" page, tick the blood pressure, give the reason, and mark it.
//   4. Record 120/78 for the same time.
//   5. For a second patient, steps 1, 3 and 4, with no check-in.
// Expected result: Step 3: "Marked: 1 reading"; both numbers of the first reading are entered in error. Step 4:
//   saved; the new systolic and diastolic share one reading, not the first's, and both have the visit of the first
//   value recorded for that time, which is none: before this step the new diastolic took the visit and the systolic
//   didn't. Step 5: the same, with no visit anywhere.
// Creator: Tahseen Arnan
public class TC_UC4_25_ReadingAcrossCheckIn(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // Records 120/80 five minutes ago, optionally checks the patient in, marks the reading and records 120/78.
    async Task<Guid> CorrectAsync(bool checkIn)
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        var taken = DateTime.UtcNow.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm");
        using var recorded = await RecordAtAsync(session.Client, patient, taken, (Systolic, "120"), (Diastolic, "80"));
        Assert.Contains("Saved:", await ReadAsync(recorded));
        if (checkIn)
        {
            using var booked = await AppointmentTests.BookAsync(session.Client, patient, clinician.Id, AppointmentTests.Day(0), "09:00", "Synthetic: blood pressure check");
            using var checkedIn = await AppointmentTests.ChangeAsync(session.Client, await AppointmentTests.IdAsync(apps, patient), "CheckIn");
            Assert.Contains("Checked in:", Words(await ReadAsync(checkedIn)));
        }

        // The diastolic was misread: the blood pressure is ticked, one reading, by its systolic.
        using var marked = await MarkAsync(apps, session.Client, patient, taken + ":00", "Synthetic: diastolic misread.", Systolic);
        Assert.Contains("Marked: 1 reading is marked entered in error", Words(await ReadAsync(marked)));
        using var corrected = await RecordAtAsync(session.Client, patient, taken, (Systolic, "120"), (Diastolic, "78"));
        Assert.Contains("Saved:", await ReadAsync(corrected));
        return patient;
    }

    [Fact]
    public async Task A_blood_pressure_corrected_after_check_in_is_one_reading_with_its_moments_visit()
    {
        var patient = await CorrectAsync(checkIn: true);

        Assert.Equal(["8480-6 120 entered-in-error none R1", "8462-4 80 entered-in-error none R1", "8480-6 120 final none R2", "8462-4 78 final none R2"],
            await ReadingsAsync(apps, patient));
        // The visit the check-in started is there: the blood pressure was taken before it and isn't tied to it.
        Assert.Equal(["in-progress"], await ProxyTests.QueryAsync(apps, "SELECT status FROM ehr.encounters WHERE patient_id = @patient", patient));
        Assert.Equal(["Synthetic: diastolic misread.", "Synthetic: diastolic misread."], await ProxyTests.QueryAsync(apps,
            "SELECT entered_in_error_reason FROM ehr.observations WHERE patient_id = @patient AND status = 'entered-in-error'", patient));
    }

    [Fact]
    public async Task A_blood_pressure_corrected_with_no_check_in_is_one_reading_too()
    {
        var patient = await CorrectAsync(checkIn: false);

        Assert.Equal(["8480-6 120 entered-in-error none R1", "8462-4 80 entered-in-error none R1", "8480-6 120 final none R2", "8462-4 78 final none R2"],
            await ReadingsAsync(apps, patient));
    }
}
