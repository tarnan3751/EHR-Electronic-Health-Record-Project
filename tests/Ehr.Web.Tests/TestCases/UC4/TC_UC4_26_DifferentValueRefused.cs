using System.Net;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VitalTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-26
// Purpose / scenario: A different value for a sign that already has one at that time is refused, saying what to do,
//   before and after the patient is checked in (step 9c, failures C and B): 120/80 recorded, then 130/80 typed for the
//   same time. Before this step, after a check-in it was saved, and two systolics stood for one moment.
// Type: Functional (invalid input)
// Requirements: FR-08, proposed FR-50
// Use case: UC 4, View patient chart
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel, who has an appointment
//   booked with them.
// Inputs and system conditions: POST /patients/{id}/vitals; POST /appointments/{id}?handler=CheckIn.
// Test data: 120/80 taken five minutes ago; then 130/80, and 130/80 with heart rate 70; a heart rate of 70 taken ten
//   minutes ago, then 72.
// Steps:
//   1. Record 120/80 taken five minutes ago. Type 130/80 for the same time.
//   2. Check the patient in. Type 130/80 for the same time again, then 130/80 with a heart rate of 70.
//   3. Record a heart rate of 70 taken ten minutes ago. Type 72 for the same time.
// Expected result: Steps 1 and 2: each is answered 422 with "Not saved:", and the systolic field says "A blood pressure
//   already stands for that time. Mark it entered in error first, then record the correct one."; what was typed is
//   still in the form; nothing is saved: 120/80 is the one reading for that time, and the heart rate wasn't saved
//   either. Taken five minutes before the check-in, the reading is tied to the visit it started (Tahseen's answer on
//   step 9c). Step 3: 422, and the heart rate field says "A value already stands for this sign at that
//   time. Mark it entered in error first, then record the correct one."
// Creator: Tahseen Arnan
public class TC_UC4_26_DifferentValueRefused(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    const string Pressure = "A blood pressure already stands for that time. Mark it entered in error first, then record the correct one.";

    static async Task RefusedAsync(HttpResponseMessage response, string field, string message, params string[] typed)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var page = await response.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("Not saved:", page);
        Assert.Matches($"(?s)id=\"vital-{field}-error\">{System.Text.RegularExpressions.Regex.Escape(message)}</p>", page);
        foreach (var value in typed)
        {
            Assert.Contains($"value=\"{value}\"", page);
        }
    }

    [Fact]
    public async Task A_different_value_for_the_same_time_is_refused_before_and_after_check_in()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        // By the database's clock, which judges the time and starts the visit.
        var started = await apps.Database.NowAsync();
        var taken = started.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm");
        using var recorded = await RecordAtAsync(session.Client, patient, taken, (Systolic, "120"), (Diastolic, "80"));
        Assert.Contains("Saved:", await ReadAsync(recorded));

        using var before = await RecordAtAsync(session.Client, patient, taken, (Systolic, "130"), (Diastolic, "80"));
        await RefusedAsync(before, Systolic, Pressure, "130", "80");

        using var booked = await AppointmentTests.BookAsync(session.Client, patient, clinician.Id, AppointmentTests.Day(0), "10:00", "Synthetic: blood pressure check");
        using var checkedIn = await AppointmentTests.ChangeAsync(session.Client, await AppointmentTests.IdAsync(apps, patient), "CheckIn");
        Assert.Contains("Checked in:", Words(await ReadAsync(checkedIn)));
        using var after = await RecordAtAsync(session.Client, patient, taken, (Systolic, "130"), (Diastolic, "80"));
        await RefusedAsync(after, Systolic, Pressure, "130", "80");
        using var withRate = await RecordAtAsync(session.Client, patient, taken, (Systolic, "130"), (Diastolic, "80"), (HeartRate, "70"));
        await RefusedAsync(withRate, Systolic, Pressure, "130", "80", "70");

        Assert.Equal(["8480-6 120 final visit R1", "8462-4 80 final visit R1"], await ReadingsAsync(apps, patient));
        Assert.Equal([$"{Diastolic} 80", $"{Systolic} 120"], await RecordedAsync(apps, patient));

        var earlier = started.AddMinutes(-10).ToString("yyyy-MM-ddTHH:mm");
        using var rate = await RecordAtAsync(session.Client, patient, earlier, (HeartRate, "70"));
        Assert.Contains("Saved:", await ReadAsync(rate));
        using var other = await RecordAtAsync(session.Client, patient, earlier, (HeartRate, "72"));
        await RefusedAsync(other, HeartRate, "A value already stands for this sign at that time. Mark it entered in error first, then record the correct one.", "72");
        Assert.Equal([$"{HeartRate} 70", $"{Diastolic} 80", $"{Systolic} 120"], await RecordedAsync(apps, patient));
    }
}
