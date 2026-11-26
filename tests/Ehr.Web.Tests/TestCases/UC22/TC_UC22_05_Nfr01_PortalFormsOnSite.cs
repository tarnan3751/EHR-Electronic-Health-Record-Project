using Xunit;

namespace Ehr.Web.Tests.TestCases.UC22;

// Test Case ID: TC-UC22-05
// Purpose / scenario: The portal's pre-visit forms pages take under 15 ms of server time at the 99th percentile on-site,
//   and under 20 ms excluding the commit off-site: the list and an open form as the patient, and the list as a proxy
//   without sensitive items, for whom the record has no forms.
// Type: Non-functional (performance)
// Requirements: NFR-01, NFR-03, NFR-04
// Use case: UC 22, Answer a pre-visit form
// Preconditions: A run of the render benchmark (bench/Ehr.Bench) on a machine doing nothing else: its own PostgreSQL
//   18 with durability on, seeded with 50,000 synthetic patients; the on-site and the off-site app in memory, the
//   off-site one with its link to the on-site server down; the benchmark's portal patient, with a PHQ-9 and a GAD-7
//   asked for before each of three appointments and five of the six answered, and a proxy grant, without sensitive
//   items, to another record.
// Inputs and system conditions: GET /portal/forms and GET /portal/forms/{the open form} as the patient, and GET
//   /portal/forms as the proxy, each 100 times to warm up and then 1,000 times, on-site and off-site.
// Test data: The synthetic clinic (bench/Ehr.Seed) and the benchmark's own forms.
// Steps:
//   1. Run the benchmark with --results, as PerformanceResults.cs says.
//   2. Run this test case against that file.
// Expected result: On-site, the p99 of the whole request, the commit included, is under 15 ms for each of the three
//   rows; off-site, the p99 excluding the commit is under 20 ms for the same three. Acceptance criterion: on-site p99 <
//   15 ms; off-site p99 excluding the commit < 20 ms; each row.
// Creator: Tahseen Arnan
public class TC_UC22_05_Nfr01_PortalFormsOnSite
{
    [Theory]
    [Trait("Category", PerformanceResults.Category)]
    [InlineData("Portal pre-visit forms, the patient's own")]
    [InlineData("Portal pre-visit form, the patient's own")]
    [InlineData("Portal pre-visit forms, as a proxy")]
    public void The_portals_forms_pages_render_within_their_budgets_at_p99(string path)
    {
        var results = PerformanceResults.Load();
        var onSite = results.Row(path);
        var offSite = results.Row("Off-site, " + path);

        Assert.True(onSite.P99 < 15, $"{path}: p99 {onSite.P99} ms, the commit included, is not under 15 ms (NFR-01).");
        Assert.True(offSite.P99ExcludingCommit < 20, $"Off-site, {path}: p99 {offSite.P99ExcludingCommit} ms excluding the commit is not under 20 ms (NFR-03).");
    }
}
