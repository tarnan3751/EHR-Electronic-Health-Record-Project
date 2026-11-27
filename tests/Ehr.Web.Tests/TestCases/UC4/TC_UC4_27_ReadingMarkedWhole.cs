using System.Net;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VitalTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-27
// Purpose / scenario: A blood pressure is marked entered in error as one reading, from one item on the mark page, and
//   corrected as a new reading with both numbers (step 9c).
// Type: Functional (alternative flow: correcting a blood pressure)
// Requirements: FR-08, proposed FR-50
// Use case: UC 4, View patient chart
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel, who has one set of
//   vital signs: 150/95 and heart rate 70, taken half an hour ago.
// Inputs and system conditions: GET and POST /patients/{id}/vitals/error?at=...; GET /patients/{id}/vitals and
//   ?showErrors=true; POST /patients/{id}/vitals.
// Test data: Reason "Synthetic: cuff too small."; then 140/90 for the same time.
// Steps:
//   1. Follow "Mark as entered in error" under the set.
//   2. Send the form with only the diastolic value's ID, as no page offers it.
//   3. Tick the blood pressure, give the reason, and mark it.
//   4. Choose "Show entries in error".
//   5. Record 140/90 for the same time.
// Expected result: Step 1: the page lists two items, "Blood pressure 150/95 mmHg", both its numbers in one item, and
//   the heart rate. Step 2: 422, "Select at least one reading."; nothing is marked.
//   Step 3: "Entered in error."; the page counts 1 reading marked; both numbers are entered in error with the reason
//   and the clinician; the heart rate stands. Step 4: 150/95 is struck through once, with the reason. Step 5: saved;
//   the flowsheet shows 140/90, a reading of its own. And a systolic marked in error alone before readings existed, as
//   the VitalReadings migration leaves one beside the reading that corrected it, is struck through with a dash for its
//   diastolic: "150/— entered in error: ...".
// Creator: Tahseen Arnan
public class TC_UC4_27_ReadingMarkedWhole(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_blood_pressure_is_marked_as_one_reading_and_corrected_as_a_new_one()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        var taken = (await apps.Database.NowAsync()).AddMinutes(-30).ToString("yyyy-MM-ddTHH:mm");
        using var recorded = await RecordAtAsync(session.Client, patient, taken, (Systolic, "150"), (Diastolic, "95"), (HeartRate, "70"));
        Assert.Contains("Saved.", await ReadAsync(recorded));

        var form = await session.Client.GetStringAsync(ErrorPath(patient, taken + ":00"), Cancel);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(form, "name=\"Chosen\"").Count);
        Assert.Contains("Blood pressure 150/95 mmHg", Words(form));
        Assert.DoesNotContain("Diastolic blood pressure", Words(form));
        Assert.DoesNotContain("Systolic blood pressure", Words(form));

        using var diastolicOnly = await MarkAsync(apps, session.Client, patient, taken + ":00", "Synthetic: cuff too small.", Diastolic);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, diastolicOnly.StatusCode);
        Assert.Contains("Select at least one reading.", Words(await diastolicOnly.Content.ReadAsStringAsync(Cancel)));
        Assert.DoesNotContain("entered-in-error", await StatesAsync(apps, patient));

        using var marked = await MarkAsync(apps, session.Client, patient, taken + ":00", "Synthetic: cuff too small.", Systolic);
        var page = await ReadAsync(marked);
        Assert.Contains("Entered in error.", Words(page));
        Assert.Contains("1 reading entered in error is not shown.", Words(page));
        Assert.Equal([$"Diastolic 95 Synthetic: cuff too small. {clinician.Id}", $"Systolic 150 Synthetic: cuff too small. {clinician.Id}"], await ProxyTests.QueryAsync(apps,
            """
            SELECT CASE code WHEN '8480-6' THEN 'Systolic' ELSE 'Diastolic' END || ' ' || trim_scale(value) || ' ' || entered_in_error_reason || ' ' || entered_in_error_by
            FROM ehr.observations WHERE patient_id = @patient AND status = 'entered-in-error' ORDER BY code
            """, patient));
        Assert.Contains($"{HeartRate} 70 final", await StatesAsync(apps, patient));

        var shown = await session.Client.GetStringAsync(Path(patient) + "?showErrors=true", Cancel);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(shown, "<span class=\"sc-struck\" data-in-error>"));
        Assert.Contains("<s>150/95</s> <span class=\"sc-struck__reason\">entered in error: Synthetic: cuff too small.</span>", shown);

        using var corrected = await RecordAtAsync(session.Client, patient, taken, (Systolic, "140"), (Diastolic, "90"));
        Assert.Matches("(?s)Blood pressure <span[^>]*>[^<]*</span></th>\\s*<td class=\"sc-table__mono\">\\s*140/90\\s*<span class=\"sc-tag sc-tag--warning\">High</span>", await ReadAsync(corrected));
        Assert.Equal(["8480-6 150 entered-in-error none R1", "8462-4 95 entered-in-error none R1", "8480-6 140 final none R2", "8462-4 90 final none R2"],
            await ReadingsAsync(apps, patient));
    }

    // A systolic marked in error alone under step 9b, its diastolic left standing and paired by the VitalReadings
    // migration with the systolic typed to correct it: a reading of its own, in error, shown with a dash for the half
    // it doesn't have.
    [Fact]
    public async Task A_blood_pressure_value_marked_alone_before_readings_is_shown_with_a_dash()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        var taken = (await apps.Database.NowAsync()).AddMinutes(-30).ToString("yyyy-MM-ddTHH:mm");
        using var recorded = await RecordAtAsync(session.Client, patient, taken, (Systolic, "140"), (Diastolic, "90"));
        Assert.Contains("Saved.", await ReadAsync(recorded));
        await ProxyTests.QueryAsync(apps,
            """
            INSERT INTO ehr.observations (id, patient_id, reading_id, category, code, display, status, value, unit, reference_low, reference_high, effective_at, issued_at,
                entered_in_error_reason, entered_in_error_at)
            SELECT gen_random_uuid(), patient_id, gen_random_uuid(), 'vital-signs', code, display, 'entered-in-error', 150, unit, reference_low, reference_high, effective_at,
                issued_at - interval '1 minute', 'Synthetic: typed 150 for 140.', now()
            FROM ehr.observations WHERE patient_id = @patient AND code = '8480-6'
            RETURNING id::text
            """, patient);

        var shown = await session.Client.GetStringAsync(Path(patient) + "?showErrors=true", Cancel);
        var row = Words(System.Text.RegularExpressions.Regex.Match(shown, "(?s)<th scope=\"row\">Blood pressure.*?</tr>").Value);
        Assert.Contains("140/90", row);
        Assert.Contains("150/— entered in error: Synthetic: typed 150 for 140.", row);
        Assert.Contains("1 reading entered in error is shown.", Words(shown));
    }
}
