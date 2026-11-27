using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VitalTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-25
// Purpose / scenario: A blood pressure belongs to the visit when it was taken up to fifteen minutes before the check-in,
//   and to none when taken earlier, whether it is typed before or after the check-in, and a correction stays one
//   reading with it (step 9c, failure A, and Tahseen's answer on step 9c: the visit is decided from the time taken
//   alone): 120/80 taken ten or twenty minutes before check-in, the reading marked entered in error, and 120/78 typed
//   for the same time. A set taken after the check-in has the visit.
// Type: Functional (alternative flow: correcting a blood pressure)
// Requirements: FR-08, proposed FR-50
// Use case: UC 4, View patient chart
// Preconditions: The clinician is signed in on the on-site app with four patients on their panel, each with an
//   appointment booked with the clinician.
// Inputs and system conditions: POST /patients/{id}/vitals; POST /appointments/{id}?handler=CheckIn; POST
//   /patients/{id}/vitals/error?at=...
// Test data: 120/80 taken ten minutes ago (two patients) or twenty minutes ago (two patients); reason "Synthetic:
//   diastolic misread."; then 120/78; heart rate 70 taken just after the check-in.
// Steps:
//   1. For one patient of each pair: record 120/80, then check them in (their visit starts now).
//   2. For the other: check them in, then record 120/80 taken before the check-in.
//   3. For each: on the set's "Mark in error" page tick the blood pressure, give the reason and mark it; record 120/78
//      for the same time; record a heart rate of 70 taken just after the check-in.
// Expected result: For every patient: "Marked in error. 1 reading"; both numbers of the first reading are entered in error; the
//   new systolic and diastolic share one reading, not the first's. Taken ten minutes before the check-in, all four
//   values have the visit, in whichever order they were typed; taken twenty minutes before, none of them has it, in
//   whichever order. The heart rate taken after the check-in has the visit.
// Creator: Tahseen Arnan
public class TC_UC4_25_ReadingAcrossCheckIn(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // Records 120/80 taken `minutesBefore` ago and checks the patient in, in either order; marks the reading and records
    // 120/78; then records a heart rate taken just after the check-in.
    async Task<Guid> CorrectAsync(bool typedFirst, int minutesBefore, string at)
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        // By the database's clock, which judges the time and starts the visit.
        var taken = (await apps.Database.NowAsync()).AddMinutes(-minutesBefore).ToString("yyyy-MM-ddTHH:mm");
        async Task RecordFirstAsync()
        {
            using var recorded = await RecordAtAsync(session.Client, patient, taken, (Systolic, "120"), (Diastolic, "80"));
            Assert.Contains("Saved.", await ReadAsync(recorded));
        }

        if (typedFirst)
        {
            await RecordFirstAsync();
        }

        using var booked = await AppointmentTests.BookAsync(session.Client, patient, clinician.Id, AppointmentTests.Day(0), at, "Synthetic: blood pressure check");
        using var checkedIn = await AppointmentTests.ChangeAsync(session.Client, await AppointmentTests.IdAsync(apps, patient), "CheckIn");
        Assert.Contains("Checked in.", Words(await ReadAsync(checkedIn)));
        if (!typedFirst)
        {
            await RecordFirstAsync();
        }

        // The diastolic was misread: the blood pressure is ticked, one reading, by its systolic.
        using var marked = await MarkAsync(apps, session.Client, patient, taken + ":00", "Synthetic: diastolic misread.", Systolic);
        Assert.Contains("Marked in error. 1 reading is now left out", Words(await ReadAsync(marked)));
        using var corrected = await RecordAtAsync(session.Client, patient, taken, (Systolic, "120"), (Diastolic, "78"));
        Assert.Contains("Saved.", await ReadAsync(corrected));
        // To the second, just after the check-in.
        using var rate = await RecordAtAsync(session.Client, patient, (await apps.Database.NowAsync()).AddSeconds(2).ToString("yyyy-MM-ddTHH:mm:ss"), (HeartRate, "70"));
        Assert.Contains("Saved.", await ReadAsync(rate));
        return patient;
    }

    // The two readings, with the visit or none, and the heart rate taken after the check-in with the visit.
    async Task VisitsAsync(Guid patient, string visit)
    {
        Assert.Equal(
            [$"8480-6 120 entered-in-error {visit} R1", $"8462-4 80 entered-in-error {visit} R1", $"8480-6 120 final {visit} R2", $"8462-4 78 final {visit} R2"],
            await ReadingsAsync(apps, patient));
        Assert.Equal(["Synthetic: diastolic misread.", "Synthetic: diastolic misread."], await ProxyTests.QueryAsync(apps,
            "SELECT entered_in_error_reason FROM ehr.observations WHERE patient_id = @patient AND status = 'entered-in-error'", patient));
        Assert.Equal(["8867-4 in-progress"], await ProxyTests.QueryAsync(apps,
            "SELECT o.code || ' ' || e.status FROM ehr.observations o JOIN ehr.encounters e ON e.id = o.encounter_id WHERE o.patient_id = @patient AND o.code = '8867-4'", patient));
    }

    [Fact]
    public async Task A_blood_pressure_taken_ten_minutes_before_check_in_and_typed_before_it_has_the_visit()
    {
        await VisitsAsync(await CorrectAsync(typedFirst: true, 10, "09:00"), "visit");
    }

    [Fact]
    public async Task A_blood_pressure_taken_ten_minutes_before_check_in_and_typed_after_it_has_the_visit()
    {
        await VisitsAsync(await CorrectAsync(typedFirst: false, 10, "10:00"), "visit");
    }

    [Fact]
    public async Task A_blood_pressure_taken_twenty_minutes_before_check_in_and_typed_before_it_has_no_visit()
    {
        await VisitsAsync(await CorrectAsync(typedFirst: true, 20, "11:00"), "none");
    }

    [Fact]
    public async Task A_blood_pressure_taken_twenty_minutes_before_check_in_and_typed_after_it_has_no_visit_either()
    {
        await VisitsAsync(await CorrectAsync(typedFirst: false, 20, "13:00"), "none");
    }
}
