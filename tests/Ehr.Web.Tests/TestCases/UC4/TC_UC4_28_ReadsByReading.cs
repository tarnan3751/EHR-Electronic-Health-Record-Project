using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VitalTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-28
// Purpose / scenario: Every page that shows a blood pressure shows one reading's two numbers as one value, flagged when
//   either is outside its range (step 9c): the flowsheet, the chart's newest value and its visit, the trends and the
//   desktop app's offline copy.
// Type: Functional (successful flow, continued: blood pressure as one reading)
// Requirements: FR-08, proposed FR-48, proposed FR-50
// Use case: UC 4, View patient chart
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel, who has a visit that
//   began half an hour ago.
// Inputs and system conditions: POST and GET /patients/{id}/vitals; GET /patients/{id}; POST
//   /api/desktop/offline-panel.
// Test data: Two days ago, 130/95 and heart rate 74; ten minutes ago, 118/92 and heart rate 70: a systolic inside its
//   range and a diastolic above it.
// Steps:
//   1. Record the two sets.
//   2. Open the chart, and the desktop app's offline copy.
// Expected result: Step 1: the flowsheet has one "Blood pressure" row, 118/92 "High" and 130/95 "High", and no row
//   for either number alone; the trends chart systolic and diastolic over the same two readings. Step 2: the chart's
//   newest blood pressure is "118/92 mmHg" "High" with a mark and the words "down from 130/95"; its visit says
//   "blood pressure 118/92 mmHg"; the offline copy's visit has 118 and 92.
// Creator: Tahseen Arnan
public class TC_UC4_28_ReadsByReading(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_blood_pressure_is_shown_as_one_readings_two_numbers_everywhere()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        await apps.InsertVisitAsync(patient, (await apps.Database.NowAsync()).AddMinutes(-30), reason: "Synthetic follow-up");
        using var earlier = await RecordAsync(apps, session.Client, patient, 2 * 24 * 60, (Systolic, "130"), (Diastolic, "95"), (HeartRate, "74"));
        using var latest = await RecordAsync(apps, session.Client, patient, 10, (Systolic, "118"), (Diastolic, "92"), (HeartRate, "70"));
        var page = await ReadAsync(latest);

        Assert.Matches(
            "(?s)<th scope=\"row\">Blood pressure <span class=\"sc-muted\">\\(mmHg\\)</span></th>\\s*<td class=\"sc-table__mono\">\\s*118/92\\s*<span class=\"sc-tag sc-tag--warning\">High</span>\\s*</td>\\s*<td class=\"sc-table__mono\">\\s*130/95\\s*<span class=\"sc-tag sc-tag--warning\">High</span>",
            page);
        Assert.DoesNotContain("<th scope=\"row\">Systolic blood pressure", page);
        Assert.DoesNotContain("<th scope=\"row\">Diastolic blood pressure", page);
        Assert.Contains("Systolic blood pressure: 2 values from", Words(page));
        Assert.Contains("Diastolic blood pressure: 2 values from", Words(page));

        var chart = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.Matches(
            "(?s)data-latest-vitals.*?<strong>Blood pressure</strong>\\s*118/92 mmHg\\s*<span class=\"sc-tag sc-tag--warning\">High</span>\\s*<span class=\"sc-trendmark\" data-direction=\"down\">.*?down from 130/95</span>",
            chart);
        Assert.DoesNotContain("<strong>Systolic blood pressure</strong>", chart);
        Assert.Contains("blood pressure 118/92 mmHg, heart rate 70/min", Words(chart));

        var token = await apps.DesktopSignInAsync(apps.OnPrem, clinician);
        using var offline = await DesktopSignInTests.PostAsync(apps.OnPrem, "/api/desktop/offline-panel", token);
        Assert.Contains("\"systolicMmHg\":118,\"diastolicMmHg\":92,\"heartRatePerMinute\":70", await offline.Content.ReadAsStringAsync(Cancel));
    }
}
