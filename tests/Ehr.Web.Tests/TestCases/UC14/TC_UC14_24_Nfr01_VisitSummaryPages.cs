using Xunit;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-24
// Purpose / scenario: The pages that write and withdraw a visit summary, and the signed note's page that lists its
//   summaries, take under 15 ms of server time at the 99th percentile on-site, and under 20 ms excluding the commit
//   off-site.
// Type: Non-functional (performance)
// Requirements: NFR-01, NFR-03, NFR-04
// Use case: UC 14, Review and sign a clinical note
// Preconditions: A run of the render benchmark (bench/Ehr.Bench) on a machine doing nothing else: its own PostgreSQL
//   18 with durability on, seeded with 50,000 synthetic patients; the on-site and the off-site app in memory, the
//   off-site one with its link to the on-site server down; a clinician with eight patients on their panel who each have a
//   signed note with three addenda, and two patients each with six signed notes and a summary of each, two sensitive and
//   one withdrawn and released again.
// Inputs and system conditions: GET /patients/{id}/notes/{note}/summary for the eight notes, GET
//   /patients/{id}/notes/{note}/summaries/{summary}/withdraw for the twelve summaries, and the notes' own page, each 100
//   times to warm up and then 1,000 times, on-site and off-site.
// Test data: The synthetic clinic (bench/Ehr.Seed) and the benchmark's own notes and summaries.
// Steps:
//   1. Run the benchmark with --results, as PerformanceResults.cs says.
//   2. Run this test case against that file.
// Expected result: On-site, the p99 of the whole request, the commit included, is under 15 ms for each of "Visit
//   summary form, a patient on the panel", "Withdraw summary form, a patient on the panel" and "Signed note with
//   addenda, a patient on the panel". Off-site, the p99 excluding the commit is under 20 ms for the same three rows.
//   Acceptance criterion: on-site p99 < 15 ms; off-site p99 excluding the commit < 20 ms; each row.
// Creator: Tahseen Arnan
public class TC_UC14_24_Nfr01_VisitSummaryPages
{
    [Theory]
    [Trait("Category", PerformanceResults.Category)]
    [InlineData("Visit summary form, a patient on the panel")]
    [InlineData("Withdraw summary form, a patient on the panel")]
    [InlineData("Signed note with addenda, a patient on the panel")]
    public void The_summary_pages_render_within_their_budgets_at_p99(string path)
    {
        var results = PerformanceResults.Load();
        var onSite = results.Row(path);
        var offSite = results.Row("Off-site, " + path);

        Assert.True(onSite.P99 < 15, $"{path}: p99 {onSite.P99} ms, the commit included, is not under 15 ms (NFR-01).");
        Assert.True(offSite.P99ExcludingCommit < 20, $"Off-site, {path}: p99 {offSite.P99ExcludingCommit} ms excluding the commit is not under 20 ms (NFR-03).");
    }
}
