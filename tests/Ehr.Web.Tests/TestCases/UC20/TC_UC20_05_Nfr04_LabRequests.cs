using Xunit;

namespace Ehr.Web.Tests.TestCases.UC20;

// Test Case ID: TC-UC20-05
// Purpose / scenario: The lab's door answers within the render budget with the clinic at full size: handing a lab ten
//   new orders, with an audit event for each, and filing one order's results through the database's rules, the order
//   resulted and its signer told, each under 20 ms of server time at the 99th percentile, excluding the commit.
// Type: Non-functional (performance)
// Requirements: NFR-04, with AGENTS.md's render-time gate
// Use case: UC 20, Receive results from a lab
// Preconditions: A run of the render benchmark (bench/Ehr.Bench) on a machine doing nothing else: its own PostgreSQL
//   18 with durability on, seeded with 50,000 synthetic patients; the on-site app in memory, with the lab's key; a
//   clinician with 100 patients on their panel.
// Inputs and system conditions: GET /api/lab/orders?since={watermark} with the lab's key, with ten complete blood
//   counts signed for ten of the panel since that watermark, 100 times to warm up and then 1,000 times; POST
//   /api/lab/orders/{id}/results with the lab's key, a complete blood count's four results each time, for 1,100 orders
//   signed for the panel, one request each.
// Test data: The synthetic clinic (bench/Ehr.Seed); hemoglobin 13.5 g/dL, hematocrit 41 %, leukocytes 6.2 and
//   platelets 250 10*3/uL.
// Steps:
//   1. Run the benchmark with --results, as PerformanceResults.cs says.
//   2. Run this test case against that file.
// Expected result: The p99 excluding the commit is under 20 ms for the rows "Lab orders, ten waiting since the last
//   ask" and "Lab results, one order filed". Acceptance criteria: p99 excluding the commit < 20 ms, each.
// Creator: Tahseen Arnan
public class TC_UC20_05_Nfr04_LabRequests
{
    const double Milliseconds = 20;

    public static TheoryData<string> Rows => ["Lab orders, ten waiting since the last ask", "Lab results, one order filed"];

    [Theory]
    [Trait("Category", PerformanceResults.Category)]
    [MemberData(nameof(Rows))]
    public void The_labs_door_answers_in_under_20_ms_of_server_time_at_p99(string row)
    {
        var measured = PerformanceResults.Load().Row(row);

        Assert.True(measured.P99ExcludingCommit < Milliseconds,
            $"{row}: p99 {measured.P99ExcludingCommit} ms excluding the commit is not under {Milliseconds} ms (NFR-04).");
    }
}
