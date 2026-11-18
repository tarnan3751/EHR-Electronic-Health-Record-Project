using Xunit;

namespace Ehr.Web.Tests.TestCases.UC18;

// Test Case ID: TC-UC18-05
// Purpose / scenario: On-site, the pages proxy access adds or changes each take under 15 ms of server time at the 99th
//   percentile: the clinic's proxy page, the portal's Access page, and the portal's home and results for a session
//   looking at someone else's record, where every page first checks the grant and every row-level-security policy
//   finds its patient through it.
// Type: Non-functional (performance)
// Requirements: NFR-01, NFR-04
// Use case: UC 18, Give someone proxy access to a record
// Preconditions: A run of the render benchmark (bench/Ehr.Bench) on a machine doing nothing else: its own PostgreSQL
//   18 with durability on, seeded with 50,000 synthetic patients; the on-site app in memory; a clinician with 100
//   patients on their panel; a portal account that the clinician has recorded as a proxy for one of them.
// Inputs and system conditions: GET /patients/{id}/proxies, GET /portal/access, and, after choosing the proxied
//   record, GET /portal and GET /portal/results; each 100 times to warm up and then 1,000 times.
// Test data: The synthetic clinic (bench/Ehr.Seed) and the benchmark's own grant.
// Steps:
//   1. Run the benchmark with --results, as PerformanceResults.cs says.
//   2. Run this test case against that file.
// Expected result: The p99 of the whole request, the commit of its audit event included, is under 15 ms for each of
//   the rows "Proxy access, a patient on the panel", "Portal access, the patient's own", "Portal choose, a record
//   shared" (the page after sign-in, step 6b), "Portal home, as a proxy" and "Portal results, as a proxy". Acceptance criterion: p99 < 15 ms, each.
// Creator: Tahseen Arnan
public class TC_UC18_05_Nfr01_ProxyPagesOnSite
{
    const double LimitMilliseconds = 15;

    [Theory]
    [Trait("Category", PerformanceResults.Category)]
    [InlineData("Proxy access, a patient on the panel")]
    [InlineData("Portal access, the patient's own")]
    [InlineData("Portal choose, a record shared")]
    [InlineData("Portal home, as a proxy")]
    [InlineData("Portal results, as a proxy")]
    public void On_site_the_proxy_pages_render_in_under_15_ms_at_p99(string path)
    {
        var row = PerformanceResults.Load().Row(path);

        Assert.True(row.P99 < LimitMilliseconds, $"{path}: p99 {row.P99} ms, the commit included, is not under {LimitMilliseconds} ms (NFR-01).");
    }
}
