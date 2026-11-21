using Xunit;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-56
// Purpose / scenario: The portal's refill page takes under 15 ms of server time at the 99th percentile on-site, and
//   under 20 ms excluding the commit off-site, as the patient and as a proxy.
// Type: Non-functional (performance)
// Requirements: NFR-01, NFR-03, NFR-04
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: A run of the render benchmark (bench/Ehr.Bench) on a machine doing nothing else: its own PostgreSQL
//   18 with durability on, seeded with 50,000 synthetic patients; the on-site and the off-site app in memory, the
//   off-site one with its link to the on-site server down; a portal account for a seeded patient, who is also a proxy
//   for another, each record with a request for every medication it may ask to renew, the first of each declined.
// Inputs and system conditions: GET /portal/refills as the patient and as the proxy, each 100 times to warm up and then
//   1,000 times, on-site and off-site.
// Test data: The synthetic clinic (bench/Ehr.Seed) and the benchmark's own requests.
// Steps:
//   1. Run the benchmark with --results, as PerformanceResults.cs says.
//   2. Run this test case against that file.
// Expected result: On-site, the p99 of the whole request, the commit included, is under 15 ms for "Portal refills, the
//   patient's own" and "Portal refills, as a proxy"; off-site, the p99 excluding the commit is under 20 ms for the same
//   two. Acceptance criterion: on-site p99 < 15 ms; off-site p99 excluding the commit < 20 ms; each row.
// Creator: Tahseen Arnan
public class TC_UC6_56_Nfr01_RefillPagesOnSite
{
    [Theory]
    [Trait("Category", PerformanceResults.Category)]
    [InlineData("Portal refills, the patient's own")]
    [InlineData("Portal refills, as a proxy")]
    public void The_portal_refill_page_renders_within_its_budgets_at_p99(string path)
    {
        var results = PerformanceResults.Load();
        var onSite = results.Row(path);
        var offSite = results.Row("Off-site, " + path);

        Assert.True(onSite.P99 < 15, $"{path}: p99 {onSite.P99} ms, the commit included, is not under 15 ms (NFR-01).");
        Assert.True(offSite.P99ExcludingCommit < 20, $"Off-site, {path}: p99 {offSite.P99ExcludingCommit} ms excluding the commit is not under 20 ms (NFR-03).");
    }
}
