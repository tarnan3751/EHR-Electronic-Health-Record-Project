using System.Text.RegularExpressions;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VitalTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-29
// Purpose / scenario: A child's blood pressure at or above the AAP's screening value for their age and sex is shown as
//   needing follow-up, everywhere it is flagged, and never as High or abnormal; just below it, it isn't flagged (step
//   9d: the table is for screening only).
// Type: Functional (alternative flow: a child's blood pressure)
// Requirements: FR-08, FR-16, proposed FR-50
// Use case: UC 4, View patient chart; UC 6, View test results
// Preconditions: The clinician is signed in on the on-site app with a boy aged 1 year 6 months on their panel; Table 6's
//   values for a boy of 1 are 98/52. The boy's portal account is signed in.
// Inputs and system conditions: POST and GET /patients/{id}/vitals; GET /patients/{id}; GET /portal/results.
// Test data: Three days ago 97.9/50, two days ago 98/50, yesterday 98.1/50.
// Steps:
//   1. Record the three sets.
//   2. Open the chart, and the boy's results in the portal.
// Expected result: Step 1: the flowsheet's blood pressure row shows 98.1/50 and 98/50 tagged "Needs follow-up" and
//   97.9/50 untagged; the systolic trend marks the two as diamonds, its table says "needs follow-up" beside them and
//   nothing beside 97.9, its sentence says "Needs follow-up at or above 98 mmHg." and that values at or above the
//   screening value need follow-up. Step 2: the chart's newest blood pressure, 98.1/50 mmHg, is tagged "Needs
//   follow-up"; the portal's systolic trend says the same in its sentence and table. Nowhere is the boy's blood
//   pressure called High, abnormal or outside the range.
// Creator: Tahseen Arnan
public class TC_UC4_29_ChildBloodPressureNeedsFollowUp(WebApps apps) : IClassFixture<WebApps>
{
    // The systolic trend's figure on a page.
    static string Systolic(string page) => Regex.Match(page, $"(?s)<figure class=\"sc-trend\" data-trend=\"{VitalTests.Systolic}\">.*?</figure>").Value;

    [Fact]
    public async Task A_childs_blood_pressure_at_or_above_its_screening_value_needs_follow_up_everywhere_and_is_never_high()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        await ProxyTests.QueryAsync(apps,
            "UPDATE ehr.patients SET gender = 'male', birth_date = (now() - interval '1 year 6 months')::date WHERE id = @patient RETURNING id::text", patient);
        var portal = await PortalResultsTests.SignInPatientAsync(apps, apps.OnPrem, patient);

        foreach (var (daysAgo, systolic) in new[] { (3, "97.9"), (2, "98"), (1, "98.1") })
        {
            using var recorded = await RecordAsync(apps, session.Client, patient, daysAgo * 24 * 60, (VitalTests.Systolic, systolic), (Diastolic, "50"));
            Assert.Contains("Saved.", await ReadAsync(recorded));
        }

        var flowsheet = await session.Client.GetStringAsync(Path(patient), Cancel);
        var row = Regex.Match(flowsheet, "(?s)<th scope=\"row\">Blood pressure.*?</tr>").Value;
        Assert.Matches(
            "(?s)<td class=\"sc-table__mono\">\\s*98.1/50\\s*<span class=\"sc-tag sc-tag--warning\">Needs follow-up</span>\\s*</td>\\s*"
            + "<td class=\"sc-table__mono\">\\s*98/50\\s*<span class=\"sc-tag sc-tag--warning\">Needs follow-up</span>\\s*</td>\\s*<td class=\"sc-table__mono\">\\s*97.9/50\\s*</td>",
            row);
        var trend = Systolic(flowsheet);
        Assert.Equal(2, Regex.Matches(trend, "sc-trend__point--outside").Count);
        Assert.Contains("98.1, needs follow-up", Words(trend));
        Assert.Contains("98, needs follow-up", Words(trend));
        Assert.Matches("<td class=\"sc-table__mono\">97.9</td>", trend);
        Assert.Contains("Needs follow-up at or above 98 mmHg.", Words(trend));
        Assert.Contains("Values at or above the shaded band need follow-up and are diamonds.", Words(trend));

        var chart = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);
        var latest = Regex.Match(chart, "(?s)<li>\\s*<strong>Blood pressure</strong>.*?</li>").Value;
        Assert.Matches("(?s)98.1/50 mmHg\\s*<span class=\"sc-tag sc-tag--warning\">Needs follow-up</span>", latest);

        var results = await portal.Client.GetStringAsync("/portal/results", Cancel);
        var portalTrend = Systolic(results);
        Assert.Contains("Needs follow-up at or above 98 mmHg.", Words(portalTrend));
        Assert.Contains("98.1, needs follow-up", Words(portalTrend));

        foreach (var view in new[] { row, trend, latest, portalTrend })
        {
            Assert.DoesNotContain("High", view);
            Assert.DoesNotContain("bnormal", view);
            Assert.DoesNotContain("outside the range", Words(view));
        }
    }
}
