using Xunit;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-11
// Purpose / scenario: On-site, a signed note's page with its addenda, and the page that signs an addendum, take under
//   15 ms of server time at the 99th percentile.
// Type: Non-functional (performance)
// Requirements: NFR-01, NFR-04
// Use case: UC 14, Review and sign a clinical note
// Preconditions: A run of the render benchmark (bench/Ehr.Bench) on a machine doing nothing else: its own PostgreSQL
//   18 with durability on, seeded with 50,000 synthetic patients; the on-site app in memory; a clinician with eight
//   patients on their panel who each have a signed note with three addenda, one of them sensitive.
// Inputs and system conditions: GET /patients/{id}/notes/{note} and GET /patients/{id}/notes/{note}/addendum, each
//   100 times to warm up and then 1,000 times.
// Test data: The synthetic clinic (bench/Ehr.Seed) and the benchmark's own notes and addenda.
// Steps:
//   1. Run the benchmark with --results, as PerformanceResults.cs says.
//   2. Run this test case against that file.
// Expected result: The p99 of the whole request, the commit of its audit event included, is under 15 ms for each of
//   the rows "Signed note with addenda, a patient on the panel" and "Addendum form, a patient on the panel".
//   Acceptance criterion: p99 < 15 ms, each.
// Creator: Tahseen Arnan
public class TC_UC14_11_Nfr01_NoteWithAddendaOnSite
{
    const double LimitMilliseconds = 15;

    [Theory]
    [Trait("Category", PerformanceResults.Category)]
    [InlineData("Signed note with addenda, a patient on the panel")]
    [InlineData("Addendum form, a patient on the panel")]
    public void On_site_the_note_and_addendum_pages_render_in_under_15_ms_at_p99(string path)
    {
        var row = PerformanceResults.Load().Row(path);

        Assert.True(row.P99 < LimitMilliseconds, $"{path}: p99 {row.P99} ms, the commit included, is not under {LimitMilliseconds} ms (NFR-01).");
    }
}
