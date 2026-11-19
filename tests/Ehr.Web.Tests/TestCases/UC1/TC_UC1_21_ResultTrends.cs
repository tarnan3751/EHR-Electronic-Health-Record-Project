using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.ResultTests;

namespace Ehr.Web.Tests.TestCases.UC1;

// Test Case ID: TC-UC1-21
// Purpose / scenario: An order's results page shows how each test has moved: a trend of this result with the
//   patient's earlier results of the same test, and none for a test with a single value.
// Type: Functional (successful flow, continued: trends of results)
// Requirements: FR-05, proposed FR-48
// Use case: UC 1, Place an order
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel.
// Inputs and system conditions: GET and POST /patients/{id}/orders/{order}/results, for two orders.
// Test data: A complete blood count with hemoglobin 9.1 g/dL; a second one with hemoglobin 13.5 g/dL and platelets 250.
// Steps:
//   1. Sign a complete blood count and file hemoglobin 9.1.
//   2. Sign another and file hemoglobin 13.5 and platelets 250.
// Expected result: Step 1: the results page has no "Trends". Step 2: "Trends" has one chart, for hemoglobin, saying
//   "2 values", "Latest 13.5 g/dL, up from 9.1" and "Reference range 12 to 17.5", with one value marked outside the
//   range and both in its table; platelets, with one value, has none.
// Creator: Tahseen Arnan
public class TC_UC1_21_ResultTrends(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_results_page_charts_each_test_that_has_earlier_values()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        var earlier = await SignedAsync(session.Client, patient, "lab", "Complete blood count (CBC)", Cbc);
        using var first = await FileAsync(session.Client, patient, earlier, (Hemoglobin, "9.1"));
        Assert.DoesNotContain("data-trend", await ReadAsync(first));

        var order = await SignedAsync(session.Client, patient, "lab", "Complete blood count (CBC)", Cbc);
        using var second = await FileAsync(session.Client, patient, order, (Hemoglobin, "13.5"), (Platelets, "250"));
        var page = await ReadAsync(second);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "<figure class=\"sc-trend\""));
        Assert.Contains($"data-trend=\"{Hemoglobin}\"", page);
        Assert.Contains("2 values", Words(page));
        Assert.Contains("Latest 13.5 g/dL, up from 9.1. Reference range 12 to 17.5.", Words(page));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "sc-trend__point--outside"));
        Assert.Matches("(?s)<td class=\"sc-table__mono\">13.5</td>.*?<td class=\"sc-table__mono\">9.1, outside the range</td>", page);
    }
}
