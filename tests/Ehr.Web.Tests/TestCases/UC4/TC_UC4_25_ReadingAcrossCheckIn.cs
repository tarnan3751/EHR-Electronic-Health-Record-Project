using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VitalTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-25
// Purpose / scenario: A blood pressure taken before the patient was checked in has no visit, whether it is typed before
//   or after the check-in, and a correction stays one reading with it (step 9c, failure A: the visit is decided from the
//   time taken, not the moment typed): 120/80 taken five minutes before check-in, the reading marked entered in error,
//   and 120/78 typed for the same time. A set taken after the check-in has the visit.
// Type: Functional (alternative flow: correcting a blood pressure)
// Requirements: FR-08, proposed FR-50
// Use case: UC 4, View patient chart
// Preconditions: The clinician is signed in on the on-site app with two patients on their panel, each with an
//   appointment booked with the clinician.
// Inputs and system conditions: POST /patients/{id}/vitals; POST /appointments/{id}?handler=CheckIn; POST
//   /patients/{id}/vitals/error?at=...
// Test data: 120/80 taken five minutes ago; reason "Synthetic: diastolic misread."; then 120/78; heart rate 70 taken
//   just after the check-in.
// Steps:
//   1. For the first patient: record 120/80 taken five minutes ago, then check them in (their visit starts now).
//   2. For the second patient: check them in, then record 120/80 taken five minutes ago, before the check-in.
//   3. For each: on the set's "Mark in error" page tick the blood pressure, give the reason and mark it; record 120/78
//      for the same time; record a heart rate of 70 taken just after the check-in.
// Expected result: For both patients alike: "Marked: 1 reading"; both numbers of the first reading are entered in
//   error; the new systolic and diastolic share one reading, not the first's; all four have no visit, since the visit
//   began after they were taken, in whichever order they were typed (before this step the second patient's reading,
//   and the first's correction, took the visit). The heart rate taken after the check-in has the visit.
// Creator: Tahseen Arnan
public class TC_UC4_25_ReadingAcrossCheckIn(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // Records 120/80 five minutes ago and checks the patient in, in either order; marks the reading and records 120/78;
    // then records a heart rate taken just after the check-in.
    async Task<Guid> CorrectAsync(bool typedFirst, string at)
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        var taken = DateTime.UtcNow.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm");
        async Task RecordFirstAsync()
        {
            using var recorded = await RecordAtAsync(session.Client, patient, taken, (Systolic, "120"), (Diastolic, "80"));
            Assert.Contains("Saved:", await ReadAsync(recorded));
        }

        if (typedFirst)
        {
            await RecordFirstAsync();
        }

        using var booked = await AppointmentTests.BookAsync(session.Client, patient, clinician.Id, AppointmentTests.Day(0), at, "Synthetic: blood pressure check");
        using var checkedIn = await AppointmentTests.ChangeAsync(session.Client, await AppointmentTests.IdAsync(apps, patient), "CheckIn");
        Assert.Contains("Checked in:", Words(await ReadAsync(checkedIn)));
        if (!typedFirst)
        {
            await RecordFirstAsync();
        }

        // The diastolic was misread: the blood pressure is ticked, one reading, by its systolic.
        using var marked = await MarkAsync(apps, session.Client, patient, taken + ":00", "Synthetic: diastolic misread.", Systolic);
        Assert.Contains("Marked: 1 reading is marked entered in error", Words(await ReadAsync(marked)));
        using var corrected = await RecordAtAsync(session.Client, patient, taken, (Systolic, "120"), (Diastolic, "78"));
        Assert.Contains("Saved:", await ReadAsync(corrected));
        // To the second, just after the check-in.
        using var rate = await RecordAtAsync(session.Client, patient, DateTime.UtcNow.AddSeconds(2).ToString("yyyy-MM-ddTHH:mm:ss"), (HeartRate, "70"));
        Assert.Contains("Saved:", await ReadAsync(rate));
        return patient;
    }

    async Task NoVisitButTheHeartRateAsync(Guid patient)
    {
        Assert.Equal(["8480-6 120 entered-in-error none R1", "8462-4 80 entered-in-error none R1", "8480-6 120 final none R2", "8462-4 78 final none R2"],
            await ReadingsAsync(apps, patient));
        Assert.Equal(["Synthetic: diastolic misread.", "Synthetic: diastolic misread."], await ProxyTests.QueryAsync(apps,
            "SELECT entered_in_error_reason FROM ehr.observations WHERE patient_id = @patient AND status = 'entered-in-error'", patient));
        // The visit the check-in started is there, and only the heart rate taken after it is tied to it.
        Assert.Equal(["8867-4 in-progress"], await ProxyTests.QueryAsync(apps,
            "SELECT o.code || ' ' || e.status FROM ehr.observations o JOIN ehr.encounters e ON e.id = o.encounter_id WHERE o.patient_id = @patient", patient));
    }

    [Fact]
    public async Task A_blood_pressure_typed_before_check_in_and_corrected_after_it_has_no_visit()
    {
        await NoVisitButTheHeartRateAsync(await CorrectAsync(typedFirst: true, "09:00"));
    }

    [Fact]
    public async Task A_blood_pressure_taken_before_check_in_but_typed_after_it_has_no_visit_either()
    {
        await NoVisitButTheHeartRateAsync(await CorrectAsync(typedFirst: false, "10:00"));
    }
}
