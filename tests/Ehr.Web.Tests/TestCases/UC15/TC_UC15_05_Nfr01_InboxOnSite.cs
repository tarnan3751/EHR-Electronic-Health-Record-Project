using Xunit;

namespace Ehr.Web.Tests.TestCases.UC15;

// Test Case ID: TC-UC15-05
// Purpose / scenario: On-site, the inbox takes under 15 ms of server time at the 99th percentile, and counting it for
//   the app bar doesn't push any other clinical page over that.
// Type: Non-functional (performance)
// Requirements: NFR-01, NFR-04
// Use case: UC 15, Work through the inbox
// Preconditions: A run of the render benchmark (bench/Ehr.Bench) on a machine doing nothing else: its own PostgreSQL
//   18 with durability on, seeded with 50,000 synthetic patients; the on-site app in memory; a clinician signed in
//   with 100 patients on their panel and 40 items in their inbox that need action, about 40 different patients.
// Inputs and system conditions: GET /inbox, and GET /patients/{id}, each 100 times to warm up and then 1,000 times.
// Test data: The synthetic clinic (bench/Ehr.Seed) and the benchmark's own inbox items.
// Steps:
//   1. Run the benchmark with --results, as PerformanceResults.cs says.
//   2. Run this test case against that file.
// Expected result: The p99 of the whole request, the commit of its audit events included, is under 15 ms for the rows
//   "Inbox, 40 items" and "Chart, a patient on the panel" (which now counts the inbox). Acceptance criterion: p99 <
//   15 ms, each.
// Creator: Tahseen Arnan (expansion step 3); reviewed and run by Samir Mukshe, approving PR 2
public class TC_UC15_05_Nfr01_InboxOnSite
{
    const double LimitMilliseconds = 15;

    [Theory]
    [Trait("Category", PerformanceResults.Category)]
    [InlineData("Inbox, 40 items")]
    [InlineData("Chart, a patient on the panel")]
    public void On_site_the_inbox_and_a_page_that_counts_it_render_in_under_15_ms_at_p99(string path)
    {
        var row = PerformanceResults.Load().Row(path);

        Assert.True(row.P99 < LimitMilliseconds, $"{path}: p99 {row.P99} ms, the commit included, is not under {LimitMilliseconds} ms (NFR-01).");
    }
}
