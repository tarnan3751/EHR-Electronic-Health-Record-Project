using Xunit;

namespace Ehr.Web.Tests.TestCases.UC17;

// Test Case ID: TC-UC17-05
// Purpose / scenario: On-site, a conversation's page for a clinician, and the portal's message list and conversation
//   for a patient, each take under 15 ms of server time at the 99th percentile.
// Type: Non-functional (performance)
// Requirements: NFR-01, NFR-04
// Use case: UC 17, Answer a patient's message
// Preconditions: A run of the render benchmark (bench/Ehr.Bench) on a machine doing nothing else: its own PostgreSQL
//   18 with durability on, seeded with 50,000 synthetic patients; the on-site app in memory; a clinician with 100
//   patients on their panel, 24 of whom each have a conversation of six messages; one of those patients signed in to
//   the portal.
// Inputs and system conditions: GET /patients/{id}/messages/{thread}, GET /portal/messages and GET
//   /portal/messages/{thread}, each 100 times to warm up and then 1,000 times.
// Test data: The synthetic clinic (bench/Ehr.Seed) and the benchmark's own conversations.
// Steps:
//   1. Run the benchmark with --results, as PerformanceResults.cs says.
//   2. Run this test case against that file.
// Expected result: The p99 of the whole request, the commit of its audit event included, is under 15 ms for each of
//   the rows "Conversation, a patient on the panel", "Portal messages, the patient's own" and "Portal conversation, the
//   patient's own". Acceptance criterion: p99 < 15 ms, each.
// Creator: Tahseen Arnan
public class TC_UC17_05_Nfr01_MessagesOnSite
{
    const double LimitMilliseconds = 15;

    [Theory]
    [Trait("Category", PerformanceResults.Category)]
    [InlineData("Conversation, a patient on the panel")]
    [InlineData("Portal messages, the patient's own")]
    [InlineData("Portal conversation, the patient's own")]
    public void On_site_the_message_pages_render_in_under_15_ms_at_p99(string path)
    {
        var row = PerformanceResults.Load().Row(path);

        Assert.True(row.P99 < LimitMilliseconds, $"{path}: p99 {row.P99} ms, the commit included, is not under {LimitMilliseconds} ms (NFR-01).");
    }
}
