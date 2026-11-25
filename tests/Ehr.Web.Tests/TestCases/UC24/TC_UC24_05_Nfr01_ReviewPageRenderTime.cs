using Xunit;

namespace Ehr.Web.Tests.TestCases.UC24;

// Test Case ID: TC-UC24-05
// Purpose / scenario: An emergency access's review page, and the audit page's list of the accesses waiting for review,
//   take under 15 ms of server time at the 99th percentile on-site, and under 20 ms excluding the commit off-site (the
//   expansion plan's step 18).
// Type: Non-functional (performance)
// Requirements: NFR-01, NFR-03, NFR-04
// Use case: UC 24, Review an emergency access
// Preconditions: A run of the render benchmark (bench/Ehr.Bench) on a machine doing nothing else: its own PostgreSQL
//   18 with durability on, seeded with 50,000 synthetic patients; the on-site and the off-site app in memory, the
//   off-site one with its link to the on-site server down; the benchmark clinician's year of emergency accesses to two
//   records, every one ended, half of them reviewed by the benchmark's administrator, who is signed in.
// Inputs and system conditions: GET /admin/emergency-access/{id} for any of the accesses, and GET
//   /admin/audit?Review=waiting, each 100 times to warm up and then 1,000 times, on-site and off-site.
// Test data: The synthetic clinic (bench/Ehr.Seed) and the benchmark's own accesses and reviews.
// Steps:
//   1. Run the benchmark with --results, as PerformanceResults.cs says.
//   2. Run this test case against that file.
// Expected result: On-site, the p99 of the whole request, the commit included, is under 15 ms for "Emergency access
//   review, an ended access" and "Audit, emergency accesses waiting for review"; off-site, the p99 excluding the commit
//   is under 20 ms for the same two. Acceptance criterion: on-site p99 < 15 ms; off-site p99 excluding the commit
//   < 20 ms; each row.
// Creator: Tahseen Arnan
public class TC_UC24_05_Nfr01_ReviewPageRenderTime
{
    [Theory]
    [Trait("Category", PerformanceResults.Category)]
    [InlineData("Emergency access review, an ended access")]
    [InlineData("Audit, emergency accesses waiting for review")]
    public void The_review_page_and_the_waiting_list_render_within_their_budgets_at_p99(string path)
    {
        var results = PerformanceResults.Load();
        var onSite = results.Row(path);
        var offSite = results.Row("Off-site, " + path);

        Assert.True(onSite.P99 < 15, $"{path}: p99 {onSite.P99} ms, the commit included, is not under 15 ms (NFR-01).");
        Assert.True(offSite.P99ExcludingCommit < 20, $"Off-site, {path}: p99 {offSite.P99ExcludingCommit} ms excluding the commit is not under 20 ms (NFR-03).");
    }
}
