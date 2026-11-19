using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VitalTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-14
// Purpose / scenario: A clinician records a set of vital signs from the chart. The flowsheet shows it, with the body
//   mass index worked out and the high blood pressure flagged; after a second set, each sign has a trend chart whose
//   numbers are also in a table.
// Type: Functional (successful flow, continued: vital signs and trends)
// Requirements: FR-08, proposed FR-48
// Use case: UC 4, View patient chart
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel. The patient has no
//   vital signs.
// Inputs and system conditions: GET /patients/{id}; GET and POST /patients/{id}/vitals.
// Test data: First set, a day ago: 148/92 mmHg, heart rate 72, weight 81 kg, height 180 cm. Second set, ten minutes
//   ago: 126/80 mmHg, heart rate 68.
// Steps:
//   1. On the chart, follow "Flowsheet, trends and recording".
//   2. Type the first set and choose "Record vital signs".
//   3. Type the second set and choose "Record vital signs".
// Expected result: Step 1: the page says "No vital signs recorded." and shows the form. Step 2: "Saved:"; the
//   flowsheet lists systolic 148 "High", diastolic 92 "High", heart rate 72, weight 81, height 180 and body mass
//   index 25 "High", each with the clinic's own name and unit; no trend yet; the write is in the audit log under the
//   clinician. Step 3: the flowsheet has two columns, newest first; "Trends" has a chart for systolic, diastolic and
//   heart rate and none for weight, each an SVG with a sentence saying what it shows ("down from 148") and a table of
//   its values; the systolic chart marks one value outside the range.
// Creator: Tahseen Arnan
public class TC_UC4_14_VitalSignsSuccessfulFlow(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Vital_signs_are_recorded_from_the_chart_shown_on_the_flowsheet_and_charted_over_time()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        var chart = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.Matches($"<a [^>]*href=\"{Path(patient)}\"[^>]*>Flowsheet, trends and recording</a>", chart);
        var empty = await session.Client.GetStringAsync(Path(patient), Cancel);
        Assert.Contains("No vital signs recorded.", empty);
        Assert.Contains("data-record-vitals", empty);

        using var first = await RecordAsync(session.Client, patient, 24 * 60,
            (Systolic, "148"), (Diastolic, "92"), (HeartRate, "72"), (Weight, "81"), (Height, "180"));
        var page = await ReadAsync(first);
        Assert.Contains("Saved:", page);
        Assert.Matches("(?s)Systolic blood pressure <span class=\"sc-muted\">\\(mmHg\\)</span></th>\\s*<td class=\"sc-table__mono\">\\s*148\\s*<span class=\"sc-tag sc-tag--warning\">High</span>", page);
        Assert.Matches("(?s)Body mass index \\(BMI\\) \\[Ratio\\] <span class=\"sc-muted\">\\(kg/m&#xB2;\\)</span></th>\\s*<td class=\"sc-table__mono\">\\s*25\\s*<span class=\"sc-tag sc-tag--warning\">High</span>", page);
        Assert.DoesNotContain("data-trend", page);
        Assert.Equal([$"{Weight} 81", $"{BodyMassIndex} 25", $"{Height} 180", $"{Diastolic} 92", $"{Systolic} 148", $"{HeartRate} 72"], await RecordedAsync(apps, patient));
        Assert.Equal(Enumerable.Repeat(clinician.Id.ToString(), 6), await ProxyTests.QueryAsync(apps,
            "SELECT actor_id::text FROM ehr.audit_events WHERE table_name = 'observations' AND action = 'insert' AND patient_id = @patient", patient));

        using var second = await RecordAsync(session.Client, patient, 10, (Systolic, "126"), (Diastolic, "80"), (HeartRate, "68"));
        page = await ReadAsync(second);
        Assert.Contains("The latest 2 sets of vital signs, newest first.", page);
        Assert.Matches("(?s)Systolic blood pressure <span[^>]*>[^<]*</span></th>\\s*<td class=\"sc-table__mono\">\\s*126\\s*</td>\\s*<td class=\"sc-table__mono\">\\s*148", page);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(page, "<figure class=\"sc-trend\"").Count);
        Assert.Contains($"data-trend=\"{Systolic}\"", page);
        Assert.DoesNotContain($"data-trend=\"{Weight}\"", page);
        Assert.Contains("Latest 126 mmHg, down from 148. Reference range 90 to 129.", Words(page));
        Assert.Matches($"(?s)data-trend=\"{Systolic}\".*?<svg [^>]*role=\"img\".*?<rect class=\"sc-trend__range\".*?<polyline class=\"sc-trend__line\".*?</svg>", page);
        var systolic = page[page.IndexOf($"data-trend=\"{Systolic}\"", StringComparison.Ordinal)..page.IndexOf($"data-trend=\"{Diastolic}\"", StringComparison.Ordinal)];
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(systolic, "sc-trend__point--outside"));
        Assert.Matches("(?s)<summary>The values as a table</summary>.*?<td class=\"sc-table__mono\">126</td>.*?<td class=\"sc-table__mono\">148, outside the range</td>", systolic);
        Assert.DoesNotContain("style=", systolic);
        Assert.True(await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient) >= 3);
    }
}
