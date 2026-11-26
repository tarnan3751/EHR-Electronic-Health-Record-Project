using Xunit;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-17
// Purpose / scenario: On-site, a patient's vital signs page (the flowsheet and a trend chart for each sign, drawn by
//   the server) takes under 15 ms of server time at the 99th percentile.
// Type: Non-functional (performance)
// Requirements: NFR-01, NFR-04
// Use case: UC 4, View patient chart
// Preconditions: A run of the render benchmark (bench/Ehr.Bench) on a machine doing nothing else: its own PostgreSQL
//   18 with durability on, seeded with 50,000 synthetic patients, whose visits' vital signs are observations; the
//   on-site app in memory; a clinician with 100 patients on their panel.
// Inputs and system conditions: GET /patients/{id}/vitals, 100 times to warm up and then 1,000 times, a patient of
//   the panel picked at random each time.
// Test data: The synthetic clinic (bench/Ehr.Seed).
// Steps:
//   1. Run the benchmark with --results, as PerformanceResults.cs says.
//   2. Run this test case against that file.
// Expected result: The p99 of the whole request, the commit of its audit event included, is under 15 ms for the row
//   "Vital signs, a patient on the panel". Acceptance criterion: p99 < 15 ms.
// Creator: Tahseen Arnan
public class TC_UC4_17_Nfr01_VitalSignsOnSite
{
    const double LimitMilliseconds = 15;

    [Fact]
    [Trait("Category", PerformanceResults.Category)]
    public void On_site_the_vital_signs_page_renders_in_under_15_ms_at_p99()
    {
        const string path = "Vital signs, a patient on the panel";
        var row = PerformanceResults.Load().Row(path);

        Assert.True(row.P99 < LimitMilliseconds, $"{path}: p99 {row.P99} ms, the commit included, is not under {LimitMilliseconds} ms (NFR-01).");
    }
}
