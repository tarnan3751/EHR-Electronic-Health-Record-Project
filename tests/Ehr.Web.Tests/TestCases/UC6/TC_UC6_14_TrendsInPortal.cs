using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.ResultTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-14
// Purpose / scenario: The patient sees how their results and vital signs have moved: the portal's results page charts
//   each test and each vital sign that has more than one value, with the numbers in a table; vital signs are not
//   listed among the test results; another patient sees none of it.
// Type: Functional (successful flow, continued: trends)
// Requirements: FR-11, proposed FR-48
// Use case: UC 6, View lab results in patient portal
// Preconditions: The patient and another patient are signed in to the portal. A clinician with the patient on their
//   panel is signed in on the on-site app.
// Inputs and system conditions: GET /portal/results as each patient, after the clinician's posts.
// Test data: Two complete blood counts, hemoglobin 9.1 then 13.5 g/dL; two sets of vital signs, heart rate 72 then
//   64 with blood pressure 148/92 once.
// Steps:
//   1. The clinician files both results and records both sets of vital signs.
//   2. The patient opens "Test results".
//   3. The other patient opens "Test results".
// Expected result: Step 2: the list has the two hemoglobin results and no vital sign; "Over time" has a chart for
//   hemoglobin ("Latest 13.5 g/dL, up from 9.1"); "Vital signs" has a chart for heart rate ("Latest 64 per minute, down
//   from 72") and none for blood pressure, taken once; each chart is an SVG with its values in a table, and the page
//   has no script and no inline style. Step 3: "No test results yet. If you're expecting one, contact your care team." and no chart.
// Creator: Tahseen Arnan
public class TC_UC6_14_TrendsInPortal(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task The_portal_charts_results_and_vital_signs_over_time_for_the_patient_only()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        var portal = await PortalResultsTests.SignInPatientAsync(apps, apps.OnPrem, patient);
        var other = await PortalResultsTests.SignInPatientAsync(apps, apps.OnPrem, await apps.InsertPatientAsync("Synthetic", "Bystander"));
        foreach (var value in new[] { "9.1", "13.5" })
        {
            var order = await SignedAsync(session.Client, patient, "lab", "Complete blood count (CBC)", Cbc);
            using var filed = await FileAsync(apps, session.Client, patient, order, (Hemoglobin, value));
        }

        using var first = await VitalTests.RecordAsync(apps, session.Client, patient, 24 * 60, (VitalTests.HeartRate, "72"), (VitalTests.Systolic, "148"), (VitalTests.Diastolic, "92"));
        using var second = await VitalTests.RecordAsync(apps, session.Client, patient, 10, (VitalTests.HeartRate, "64"));
        Assert.Equal(4, (await VitalTests.RecordedAsync(apps, patient)).Count);

        var page = await portal.Client.GetStringAsync("/portal/results", Cancel);
        var list = page[page.IndexOf("<ul class=\"sc-results\">", StringComparison.Ordinal)..page.IndexOf("</ul>", StringComparison.Ordinal)];
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(list, "<details class=\"sc-result\"").Count);
        Assert.DoesNotContain("Heart rate", list);
        Assert.DoesNotContain("blood pressure", list);
        Assert.Matches($"(?s)<h2>Over time</h2>.*?data-trend=\"{Hemoglobin}\".*?<h2>Vital signs</h2>.*?data-trend=\"{VitalTests.HeartRate}\"", page);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(page, "<figure class=\"sc-trend\"").Count);
        Assert.Contains("Latest 13.5 g/dL, up from 9.1.", Words(page));
        Assert.Contains("Latest 64 per minute, down from 72.", Words(page));
        Assert.DoesNotContain($"data-trend=\"{VitalTests.Systolic}\"", page);
        Assert.Matches("(?s)<svg class=\"sc-trend__chart\"[^>]*role=\"img\".*?<summary>The values as a table</summary>", page);
        Assert.DoesNotContain(" style=", page);

        var theirs = await other.Client.GetStringAsync("/portal/results", Cancel);
        Assert.Contains("No test results yet. If you're expecting one, contact your care team.", theirs);
        Assert.DoesNotContain("data-trend", theirs);
    }
}
