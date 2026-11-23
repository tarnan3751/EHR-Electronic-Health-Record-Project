using Xunit;

namespace Ehr.Web.Tests.TestCases.UC13;

// Test Case ID: TC-UC13-14
// Purpose / scenario: The pages that show what a dictation proposes (the expansion plan's step 15): its review with the
//   orders and allergies to add listed, the order form and the allergy form opened from one, and the note editor saying
//   how many still wait, take under 15 ms of server time at the 99th percentile on-site, and under 20 ms excluding the
//   commit off-site.
// Type: Non-functional (performance)
// Requirements: NFR-01, NFR-03, NFR-04
// Use case: UC 13, Review AI-proposed note fields
// Preconditions: A run of the render benchmark (bench/Ehr.Bench) on a machine doing nothing else: its own PostgreSQL
//   18 with durability on, seeded with 50,000 synthetic patients; the on-site and the off-site app in memory, the
//   off-site one with its link to the on-site server down; a clinician with patients on their panel who each have a new
//   draft dictated with an order and an allergy to add, staged through the app's own service and the database's rules,
//   both proposals still waiting.
// Inputs and system conditions: GET the review (/patients/{id}/notes/drafts/{draft}/review/{review}), the order form
//   (/patients/{id}/orders/new?type=lab&proposal={order}), the allergy form (/patients/{id}/allergies?proposal={allergy})
//   and the note editor (/patients/{id}/notes/drafts/{draft}) for those patients, each 100 times to warm up and then
//   1,000 times, on-site and off-site (where each is read from the replica).
// Test data: The synthetic clinic (bench/Ehr.Seed) and the benchmark's dictations: "Synthetic: blood pressure is fine
//   today. Go ahead and order a lipid panel. Add penicillin to her allergy list."
// Steps:
//   1. Run the benchmark with --results, as PerformanceResults.cs says.
//   2. Run this test case against that file.
// Expected result: On-site, the p99 of the whole request, the commit included, is under 15 ms for "Note review with
//   dictation proposals, a patient on the panel", "Order form from a dictation proposal, a patient on the panel",
//   "Allergy form from a dictation proposal, a patient on the panel" and "Note editor with dictation proposals waiting,
//   a patient on the panel". Off-site, the p99 excluding the commit is under 20 ms for each. Acceptance criterion:
//   on-site p99 < 15 ms; off-site p99 excluding the commit < 20 ms; each row.
// Creator: Tahseen Arnan
public class TC_UC13_14_Nfr01_DictationProposalPages
{
    [Theory]
    [Trait("Category", PerformanceResults.Category)]
    [InlineData("Note review with dictation proposals, a patient on the panel")]
    [InlineData("Order form from a dictation proposal, a patient on the panel")]
    [InlineData("Allergy form from a dictation proposal, a patient on the panel")]
    [InlineData("Note editor with dictation proposals waiting, a patient on the panel")]
    public void The_pages_showing_dictation_proposals_render_within_their_budgets_at_p99(string path)
    {
        var results = PerformanceResults.Load();
        var onSite = results.Row(path);
        var offSite = results.Row("Off-site, " + path);

        Assert.True(onSite.P99 < 15, $"{path}: p99 {onSite.P99} ms, the commit included, is not under 15 ms (NFR-01).");
        Assert.True(offSite.P99ExcludingCommit < 20, $"Off-site, {path}: p99 {offSite.P99ExcludingCommit} ms excluding the commit is not under 20 ms (NFR-03).");
    }
}
