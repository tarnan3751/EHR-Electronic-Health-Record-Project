using Xunit;

namespace Ehr.Web.Tests.TestCases.UC21;

// Test Case ID: TC-UC21-05
// Purpose / scenario: A refill request's page, and the order form opened to approve one, take under 15 ms of server
//   time at the 99th percentile on-site, and under 20 ms excluding the commit off-site.
// Type: Non-functional (performance)
// Requirements: NFR-01, NFR-03, NFR-04
// Use case: UC 21, Answer a refill request
// Preconditions: A run of the render benchmark (bench/Ehr.Bench) on a machine doing nothing else: its own PostgreSQL
//   18 with durability on, seeded with 50,000 synthetic patients; the on-site and the off-site app in memory, the
//   off-site one with its link to the on-site server down; two patients on the benchmark clinician's panel, each with a
//   request for every medication their portal may ask to renew, the first of each declined.
// Inputs and system conditions: GET /patients/{id}/refills/{request} for any of the requests, and GET
//   /patients/{id}/orders/new?type=medication&refill={request} for the waiting ones, each 100 times to warm up and then
//   1,000 times, on-site and off-site.
// Test data: The synthetic clinic (bench/Ehr.Seed) and the benchmark's own requests.
// Steps:
//   1. Run the benchmark with --results, as PerformanceResults.cs says.
//   2. Run this test case against that file.
// Expected result: On-site, the p99 of the whole request, the commit included, is under 15 ms for "Refill request, a
//   patient on the panel" and "Order form approving a refill, a patient on the panel"; off-site, the p99 excluding the
//   commit is under 20 ms for the same two. Acceptance criterion: on-site p99 < 15 ms; off-site p99 excluding the commit
//   < 20 ms; each row.
// Creator: Tahseen Arnan
public class TC_UC21_05_Nfr01_RefillReviewOnSite
{
    [Theory]
    [Trait("Category", PerformanceResults.Category)]
    [InlineData("Refill request, a patient on the panel")]
    [InlineData("Order form approving a refill, a patient on the panel")]
    public void A_refill_request_and_its_order_form_render_within_their_budgets_at_p99(string path)
    {
        var results = PerformanceResults.Load();
        var onSite = results.Row(path);
        var offSite = results.Row("Off-site, " + path);

        Assert.True(onSite.P99 < 15, $"{path}: p99 {onSite.P99} ms, the commit included, is not under 15 ms (NFR-01).");
        Assert.True(offSite.P99ExcludingCommit < 20, $"Off-site, {path}: p99 {offSite.P99ExcludingCommit} ms excluding the commit is not under 20 ms (NFR-03).");
    }
}
