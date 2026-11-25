using Xunit;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-11
// Purpose / scenario: On-site, the pages added for editing the chart's lists each take under 15 ms of server time at
//   the 99th percentile: the entry form, the code search as a clinician types, the history, and the chart with its
//   sensitive items shown.
// Type: Non-functional (performance)
// Requirements: NFR-01, NFR-04
// Use case: UC 4, View a patient's chart (editing the chart's lists)
// Preconditions: A run of the render benchmark (bench/Ehr.Bench) on a machine doing nothing else: its own PostgreSQL
//   18 with fsync, synchronous_commit and full_page_writes on, seeded with 50,000 synthetic patients and the
//   reference codes; the on-site app in memory; a clinician signed in with 100 patients on their panel.
// Inputs and system conditions: GET /patients/{id}/diagnoses, GET /codes?kind=condition&q=<three letters>, GET
//   /patients/{id}/chart-history and GET /patients/{id}?sensitive=true, each 100 times to warm up and then 1,000
//   times, one after another.
// Test data: The synthetic clinic of 50,000 patients (bench/Ehr.Seed), about one in six of whose diagnoses is
//   sensitive by its code.
// Steps:
//   1. Run the benchmark with --results, as PerformanceResults.cs says.
//   2. Run this test case against that file.
// Expected result: The p99 of the whole request, the commit of its audit events included, is under 15 ms for each of
//   the four rows. Acceptance criterion: p99 < 15 ms, each.
// Creator: Tahseen Arnan (expansion step 2), UC 4 owner Diego Valenzuela
public class TC_UC4_11_Nfr01_ListPagesOnSite
{
    const double LimitMilliseconds = 15;

    [Theory]
    [Trait("Category", PerformanceResults.Category)]
    [InlineData("Chart entry form, a patient on the panel")]
    [InlineData("Code search, first 3 letters")]
    [InlineData("Chart history, a patient on the panel")]
    [InlineData("Chart with sensitive items, a patient on the panel")]
    public void On_site_the_pages_for_editing_the_charts_lists_render_in_under_15_ms_at_p99(string path)
    {
        var row = PerformanceResults.Load().Row(path);

        Assert.True(row.P99 < LimitMilliseconds, $"{path}: p99 {row.P99} ms, the commit included, is not under {LimitMilliseconds} ms (NFR-01).");
    }
}
