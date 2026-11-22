using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VitalTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-19
// Purpose / scenario: A wrong reading is marked "entered in error" with a reason. It stays in the record, struck
//   through on request, and is left out of the flowsheet, the trends, the chart, the offline copy and the portal; the
//   correct value is recorded as a new reading for the same moment.
// Type: Functional (alternative flow: correcting a vital sign)
// Requirements: FR-08, proposed FR-50
// Use case: UC 4, View patient chart
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel. The patient has two
//   sets of vital signs: yesterday's 180/92 with heart rate 72, weight 81 and height 180; and one from ten minutes ago,
//   126/80 with heart rate 68. The patient is signed in to the portal.
// Inputs and system conditions: GET and POST /patients/{id}/vitals/error?at=...; GET /patients/{id}/vitals and
//   ?showErrors=true; GET /patients/{id}; POST /api/desktop/offline-panel; GET /portal/results.
// Test data: Reason "Typed 180 for 108."; the correct systolic, 108, and the correct weight, 79.
// Steps:
//   1. On the flowsheet, follow "Mark in error" under yesterday's set. Tick the blood pressure and the weight, type
//      the reason, and choose "Mark entered in error".
//   2. Choose "Show entries in error".
//   3. Record 108/92 for yesterday's time; then 108/95; then weight 79 with height 180.
//   4. Open the chart, the offline copy, and the patient's portal results.
// Expected result: Step 1: "Marked: 2 readings"; the page says 3 readings are marked (a blood pressure is one
//   reading, both its numbers marked, step 9c; and the body mass index worked out from the weight is marked with
//   it); yesterday's column no longer has 180/92, 81 or the body mass index; there is no blood pressure trend (one of
//   each value stands). The four values are in the database as entered-in-error with the reason, the clinician and
//   the time. Step 2: 180/92, 81 and 25 are shown struck through, with the reason in words. Step 3: 108/92 is saved,
//   a new reading; yesterday's column shows 108/92 and the systolic trend returns, "Latest 126 mmHg, up from 108";
//   108/95 is refused, a blood pressure already standing for that time. Step 4: the chart's latest blood pressure is
//   126/80 "High" with no up or down mark, its numbers having moved opposite ways from 108/92; nothing anywhere
//   shows 180; the portal charts systolic from 108 to 126.
// Creator: Tahseen Arnan
public class TC_UC4_19_VitalSignEnteredInError(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_reading_marked_entered_in_error_stays_in_the_record_and_leaves_every_view_and_the_correct_value_is_a_new_reading()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        var portal = await PortalResultsTests.SignInPatientAsync(apps, apps.OnPrem, patient);
        var yesterday = (await apps.Database.NowAsync()).AddDays(-1).ToString("yyyy-MM-ddTHH:mm");
        using var wrong = await RecordAtAsync(session.Client, patient, yesterday, (Systolic, "180"), (Diastolic, "92"), (HeartRate, "72"), (Weight, "81"), (Height, "180"));
        using var later = await RecordAsync(session.Client, patient, 10, (Systolic, "126"), (Diastolic, "80"), (HeartRate, "68"));
        var flowsheet = await ReadAsync(later);
        Assert.Contains("data-mark-in-error", flowsheet);
        Assert.Contains($"/patients/{patient}/vitals/error?at={Uri.EscapeDataString(yesterday + ":00")}", flowsheet);
        var form = await session.Client.GetStringAsync(ErrorPath(patient, yesterday + ":00"), Cancel);
        Assert.Contains("Which readings are wrong?", form);
        Assert.Contains("marking the weight or the height also marks the body mass index", form);
        // The body mass index is not a reading to tick: it follows its weight and height. A blood pressure is one.
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(form, "name=\"Chosen\"").Count);

        using var marked = await MarkAsync(apps, session.Client, patient, yesterday + ":00", "Typed 180 for 108.", Systolic, Weight);
        var page = await ReadAsync(marked);
        Assert.Contains("Marked: 2 readings are marked entered in error", Words(page));
        Assert.Contains("3 readings are marked entered in error and left out.", Words(page));
        Assert.DoesNotContain("180", System.Text.RegularExpressions.Regex.Match(page, "(?s)<tbody>.*?Blood pressure.*?</tr>").Value);
        Assert.DoesNotContain($"data-trend=\"{Systolic}\"", page);
        Assert.DoesNotContain($"data-trend=\"{Diastolic}\"", page);
        Assert.DoesNotContain("sc-struck", page);
        Assert.Equal(
            [$"{Weight} 81 entered-in-error", $"{BodyMassIndex} 25 entered-in-error", $"{Height} 180 final", $"{Diastolic} 92 entered-in-error", $"{Systolic} 180 entered-in-error", $"{HeartRate} 72 final",
                $"{Diastolic} 80 final", $"{Systolic} 126 final", $"{HeartRate} 68 final"],
            await StatesAsync(apps, patient));
        Assert.Equal(Enumerable.Repeat($"Typed 180 for 108. {clinician.Id} true", 4), await ProxyTests.QueryAsync(apps,
            "SELECT entered_in_error_reason || ' ' || entered_in_error_by || ' ' || (entered_in_error_at > now() - interval '1 minute') FROM ehr.observations WHERE patient_id = @patient AND status = 'entered-in-error'",
            patient));

        var shown = await session.Client.GetStringAsync(Path(patient) + "?showErrors=true", Cancel);
        Assert.Matches("(?s)<span class=\"sc-struck\" data-in-error><s>180/92</s> <span class=\"sc-struck__reason\">entered in error: Typed 180 for 108.</span>", shown);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(shown, "<span class=\"sc-struck\" data-in-error>").Count);

        // The blood pressure typed again, both numbers: a new reading.
        using var corrected = await RecordAtAsync(session.Client, patient, yesterday, (Systolic, "108"), (Diastolic, "92"));
        page = await ReadAsync(corrected);
        Assert.Contains("Saved:", page);
        Assert.Contains("Latest 126 mmHg, up from 108.", Words(page));
        Assert.Equal(1, (await StatesAsync(apps, patient)).Count(state => state == $"{Diastolic} 92 final"));
        Assert.Contains($"{Systolic} 108 final", await StatesAsync(apps, patient));
        // A different diastolic for a moment that has a blood pressure is refused: mark the standing one in error first.
        using var clash = await RecordAtAsync(session.Client, patient, yesterday, (Systolic, "108"), (Diastolic, "95"));
        Assert.Contains("A blood pressure already stands for that time. Mark it entered in error first, then record the correct one.",
            Words(await clash.Content.ReadAsStringAsync(Cancel)));
        // The weight corrected beside the height that stands: a new body mass index is worked out.
        using var weighed = await RecordAtAsync(session.Client, patient, yesterday, (Weight, "79"), (Height, "180"));
        Assert.Contains("Saved:", await ReadAsync(weighed));
        Assert.Contains($"{BodyMassIndex} 24.4 final", await StatesAsync(apps, patient));

        var chart = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);
        // 108/92 to 126/80: one number up, the other down, so no mark (step 9c). High: the diastolic is above 79.
        Assert.Matches("(?s)<strong>Blood pressure</strong>\\s*126/80 mmHg\\s*<span class=\"sc-tag sc-tag--warning\">High</span>\\s*<span class=\"sc-muted\">", chart);
        Assert.Matches("(?s)<strong>Weight</strong>\\s*79 kg", chart);
        var token = await apps.DesktopSignInAsync(apps.OnPrem, clinician);
        using var offline = await DesktopSignInTests.PostAsync(apps.OnPrem, "/api/desktop/offline-panel", token);
        var results = await portal.Client.GetStringAsync("/portal/results", Cancel);
        Assert.Contains("Latest 126 mmHg, up from 108.", Words(results));
        foreach (var view in new[] { chart, await offline.Content.ReadAsStringAsync(Cancel), results, await session.Client.GetStringAsync(Path(patient), Cancel) })
        {
            Assert.DoesNotContain("180 mmHg", Words(view));
            Assert.DoesNotContain(">180<", view.Replace("180 cm", "", StringComparison.Ordinal));
            Assert.DoesNotContain("\"systolicMmHg\":180", view);
        }
    }

    // A whole set is wrong (the wrong patient): every reading is ticked, weight and height among them, and the body
    // mass index goes with them. Then a weight corrected by itself gets its body mass index back, and a set older than
    // the six the flowsheet lists can still be reached and corrected.
    [Fact]
    public async Task A_whole_set_is_marked_and_a_weight_corrected_alone_gets_its_body_mass_index_and_an_older_set_can_be_reached()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        var now = await apps.Database.NowAsync();
        var times = Enumerable.Range(1, 8).Select(days => now.AddDays(-days).ToString("yyyy-MM-ddTHH:mm")).ToList();
        foreach (var time in times)
        {
            using var recorded = await RecordAtAsync(session.Client, patient, time, (Systolic, "120"), (Diastolic, "70"), (HeartRate, "70"), (Weight, "81"), (Height, "180"));
            Assert.Equal(System.Net.HttpStatusCode.OK, recorded.StatusCode);
        }

        // The blood pressure is one reading, ticked by its systolic: the diastolic isn't offered, and the database marks it.
        using var whole = await MarkAsync(apps, session.Client, patient, times[0] + ":00", "Wrong patient.", Systolic, Diastolic, HeartRate, Weight, Height);
        Assert.Contains("Marked: 4 readings", Words(await ReadAsync(whole)));
        Assert.Equal(6, (await StatesAsync(apps, patient)).Count(state => state.EndsWith("entered-in-error", StringComparison.Ordinal)));

        // The weight alone, in the second set: marked, then typed again by itself. The height stands.
        using var weight = await MarkAsync(apps, session.Client, patient, times[1] + ":00", "Weighed with a coat on.", Weight);
        Assert.Contains("Marked:", Words(await ReadAsync(weight)));
        using var corrected = await RecordAtAsync(session.Client, patient, times[1], (Weight, "79"));
        Assert.Contains("Saved:", await ReadAsync(corrected));
        Assert.Contains($"{BodyMassIndex} 24.4 final", await StatesAsync(apps, patient));

        // The eighth set is beyond the six listed: "Show earlier sets" lists it, with its "Mark in error" link.
        var flowsheet = await session.Client.GetStringAsync(Path(patient), Cancel);
        var oldest = Uri.EscapeDataString(times[7] + ":00");
        Assert.DoesNotContain(oldest, flowsheet);
        Assert.Contains("data-earlier-sets", flowsheet);
        var earlier = await session.Client.GetStringAsync(Path(patient) + "?earlier=true", Cancel);
        Assert.Contains($"/patients/{patient}/vitals/error?at={oldest}", earlier);
        using var old = await MarkAsync(apps, session.Client, patient, times[7] + ":00", "Cuff too small.", Systolic);
        Assert.Contains("Marked:", Words(await ReadAsync(old)));
        // And with entries in error shown, that old one is among them.
        Assert.Contains("entered in error: Cuff too small.", await session.Client.GetStringAsync(Path(patient) + "?showErrors=true", Cancel));
    }
}
