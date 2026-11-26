using Xunit;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-32
// Purpose / scenario: The note editor, with its visit types and their prompts (the expansion plan's step 14), and the
//   vital signs it offers for Objective take under 15 ms of server time at the 99th percentile on-site, and under 20 ms
//   excluding the commit off-site.
// Type: Non-functional (performance)
// Requirements: NFR-01, NFR-03, NFR-04
// Use case: UC 14, Review and sign a clinical note
// Preconditions: A run of the render benchmark (bench/Ehr.Bench) on a machine doing nothing else: its own PostgreSQL
//   18 with durability on, seeded with 50,000 synthetic patients; the on-site and the off-site app in memory, the
//   off-site one with its link to the on-site server down; a clinician with eight patients on their panel who each have a
//   signed note and a draft written in four sections for a follow-up visit.
// Inputs and system conditions: GET /patients/{id}/notes/drafts/{draft} and its ?handler=Vitals for the eight drafts,
//   each 100 times to warm up and then 1,000 times, on-site and off-site (where the editor shows the draft as last
//   saved, and the vital signs are read from the replica).
// Test data: The synthetic clinic (bench/Ehr.Seed), whose patients' visits have vital signs, and the benchmark's drafts.
// Steps:
//   1. Run the benchmark with --results, as PerformanceResults.cs says.
//   2. Run this test case against that file.
// Expected result: On-site, the p99 of the whole request, the commit included, is under 15 ms for "Note editor, a draft
//   of a patient on the panel" and "Vital signs for a note's Objective, a patient on the panel". Off-site, the p99
//   excluding the commit is under 20 ms for both. Acceptance criterion: on-site p99 < 15 ms; off-site p99 excluding the
//   commit < 20 ms; each row.
// Creator: Tahseen Arnan
public class TC_UC14_32_Nfr01_NoteEditorAndVitalSigns
{
    [Theory]
    [Trait("Category", PerformanceResults.Category)]
    [InlineData("Note editor, a draft of a patient on the panel")]
    [InlineData("Vital signs for a note's Objective, a patient on the panel")]
    public void The_editor_and_its_vital_signs_render_within_their_budgets_at_p99(string path)
    {
        var results = PerformanceResults.Load();
        var onSite = results.Row(path);
        var offSite = results.Row("Off-site, " + path);

        Assert.True(onSite.P99 < 15, $"{path}: p99 {onSite.P99} ms, the commit included, is not under 15 ms (NFR-01).");
        Assert.True(offSite.P99ExcludingCommit < 20, $"Off-site, {path}: p99 {offSite.P99ExcludingCommit} ms excluding the commit is not under 20 ms (NFR-03).");
    }
}
