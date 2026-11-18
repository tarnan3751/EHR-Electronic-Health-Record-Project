using Xunit;

namespace Ehr.Web.Tests.TestCases.UC1;

// Test Case ID: TC-UC1-16
// Purpose / scenario: On-site, an order's results page, and the inbox when it holds results to acknowledge (each
//   checked for values outside their range so abnormal ones come first), take under 15 ms of server time at the 99th
//   percentile.
// Type: Non-functional (performance)
// Requirements: NFR-01, NFR-04
// Use case: UC 1, Place an order
// Preconditions: A run of the render benchmark (bench/Ehr.Bench) on a machine doing nothing else: its own PostgreSQL
//   18 with durability on, seeded with 50,000 synthetic patients; the on-site app in memory; a clinician with 100
//   patients on their panel, 24 of whom each have a resulted complete blood count the clinician signed, half of them
//   with a value outside its reference range.
// Inputs and system conditions: GET /patients/{id}/orders/{order}/results and GET /inbox, each 100 times to warm up
//   and then 1,000 times.
// Test data: The synthetic clinic (bench/Ehr.Seed) and the benchmark's own orders and results.
// Steps:
//   1. Run the benchmark with --results, as PerformanceResults.cs says.
//   2. Run this test case against that file.
// Expected result: The p99 of the whole request, the commit of its audit event included, is under 15 ms for each of
//   the rows "Order results, a patient on the panel" and "Inbox, with results to acknowledge". Acceptance criterion:
//   p99 < 15 ms, each.
// Creator: Tahseen Arnan
public class TC_UC1_16_Nfr01_ResultPagesOnSite
{
    const double LimitMilliseconds = 15;

    [Theory]
    [Trait("Category", PerformanceResults.Category)]
    [InlineData("Order results, a patient on the panel")]
    [InlineData("Inbox, with results to acknowledge")]
    public void On_site_the_result_pages_render_in_under_15_ms_at_p99(string path)
    {
        var row = PerformanceResults.Load().Row(path);

        Assert.True(row.P99 < LimitMilliseconds, $"{path}: p99 {row.P99} ms, the commit included, is not under {LimitMilliseconds} ms (NFR-01).");
    }
}
