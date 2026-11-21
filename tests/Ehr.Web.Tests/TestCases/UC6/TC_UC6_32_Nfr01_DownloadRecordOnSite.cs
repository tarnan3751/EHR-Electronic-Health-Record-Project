using Xunit;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-32
// Purpose / scenario: "Download my record", its file and its printable page take under 15 ms of server time at the 99th
//   percentile on-site, and under 20 ms excluding the commit off-site: as the patient, as a proxy, and for the seeded
//   patient with the most entries.
// Type: Non-functional (performance)
// Requirements: NFR-01, NFR-03, NFR-04
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: A run of the render benchmark (bench/Ehr.Bench) on a machine doing nothing else: its own PostgreSQL
//   18 with durability on, seeded with 50,000 synthetic patients; the on-site and the off-site app in memory, the
//   off-site one with its link to the on-site server down; a portal account for a seeded patient, who is also a proxy
//   for another, each with six visit summaries; and a portal account for the seeded patient whose record has the most
//   entries.
// Inputs and system conditions: GET /portal/record, /portal/record?handler=File and /portal/record/print, as the
//   patient, as the proxy and as the patient with the most entries, each 100 times to warm up and then 1,000 times,
//   on-site and off-site.
// Test data: The synthetic clinic (bench/Ehr.Seed) and the benchmark's own summaries.
// Steps:
//   1. Run the benchmark with --results, as PerformanceResults.cs says.
//   2. Run this test case against that file.
// Expected result: On-site, the p99 of the whole request, the commit included, is under 15 ms for each row; off-site, the
//   p99 excluding the commit is under 20 ms for each. Acceptance criterion: on-site p99 < 15 ms; off-site p99 excluding
//   the commit < 20 ms; each row.
// Creator: Tahseen Arnan
public class TC_UC6_32_Nfr01_DownloadRecordOnSite
{
    [Theory]
    [Trait("Category", PerformanceResults.Category)]
    [InlineData("Portal record page, the patient's own")]
    [InlineData("Portal record file, the patient's own")]
    [InlineData("Portal printable record, the patient's own")]
    [InlineData("Portal record file, as a proxy")]
    [InlineData("Portal printable record, as a proxy")]
    [InlineData("Portal record file, the largest seeded record")]
    [InlineData("Portal printable record, the largest seeded record")]
    public void The_download_and_its_pages_render_within_their_budgets_at_p99(string path)
    {
        var results = PerformanceResults.Load();
        var onSite = results.Row(path);
        var offSite = results.Row("Off-site, " + path);

        Assert.True(onSite.P99 < 15, $"{path}: p99 {onSite.P99} ms, the commit included, is not under 15 ms (NFR-01).");
        Assert.True(offSite.P99ExcludingCommit < 20, $"Off-site, {path}: p99 {offSite.P99ExcludingCommit} ms excluding the commit is not under 20 ms (NFR-03).");
    }
}
