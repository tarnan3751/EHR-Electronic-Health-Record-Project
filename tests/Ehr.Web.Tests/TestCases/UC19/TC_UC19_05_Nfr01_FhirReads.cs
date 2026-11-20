using Xunit;

namespace Ehr.Web.Tests.TestCases.UC19;

// Test Case ID: TC-UC19-05
// Purpose / scenario: The FHIR API answers as fast as the pages do: on-site under 15 ms of server time at the 99th
//   percentile, the commit of its audit event included; off-site, from the cloud copy during an outage, under 20 ms
//   excluding the commit (the queue's write, off-site), so one network round trip plus under 20 ms.
// Type: Non-functional (performance)
// Requirements: NFR-01, NFR-03, NFR-04
// Use case: UC 19, Read a patient's record through the FHIR API
// Preconditions: A run of the render benchmark (bench/Ehr.Bench) on a machine doing nothing else: its own PostgreSQL
//   18 with durability on, seeded with 50,000 synthetic patients; the on-site app, and then the cloud app with its link
//   to the on-site server down, in memory; a clinician with 100 patients on their panel.
// Inputs and system conditions: GET /fhir/Patient/{id}, GET /fhir/Observation?patient={id}, GET
//   /fhir/Condition?patient={id}&show-sensitive=true and GET /fhir/Observation/{id}, for patients on the panel, each 100
//   times to warm up and then 1,000 times, on-site and off-site.
// Test data: The synthetic clinic (bench/Ehr.Seed): each patient's seeded results and vital signs, diagnoses
//   (sensitive ones among them) and visits.
// Steps:
//   1. Run the benchmark with --results, as PerformanceResults.cs says.
//   2. Run this test case against that file.
// Expected result: On-site, the p99 of the whole request, the commit included, is under 15 ms for each of the rows
//   "FHIR patient, a patient on the panel", "FHIR observations, a patient on the panel", "FHIR conditions with sensitive
//   items, a patient on the panel" and "FHIR observation by ID, a patient on the panel". Off-site, the p99 excluding the
//   commit is under 20 ms for the same rows, starting "Off-site, ". Acceptance criteria: p99 < 15 ms on-site, and p99
//   excluding the commit < 20 ms off-site, each.
// Creator: Tahseen Arnan
public class TC_UC19_05_Nfr01_FhirReads
{
    const double OnSiteMilliseconds = 15;

    const double OffSiteMilliseconds = 20;

    public static TheoryData<string> Rows =>
    [
        "FHIR patient, a patient on the panel", "FHIR observations, a patient on the panel",
        "FHIR conditions with sensitive items, a patient on the panel", "FHIR observation by ID, a patient on the panel",
    ];

    [Theory]
    [Trait("Category", PerformanceResults.Category)]
    [MemberData(nameof(Rows))]
    public void On_site_a_fhir_read_takes_under_15_ms_at_p99(string path)
    {
        var row = PerformanceResults.Load().Row(path);

        Assert.True(row.P99 < OnSiteMilliseconds, $"{path}: p99 {row.P99} ms, the commit included, is not under {OnSiteMilliseconds} ms (NFR-01).");
    }

    [Theory]
    [Trait("Category", PerformanceResults.Category)]
    [MemberData(nameof(Rows))]
    public void Off_site_a_fhir_read_takes_under_20_ms_of_server_time_at_p99(string path)
    {
        var row = PerformanceResults.Load().Row("Off-site, " + path);

        Assert.True(row.P99ExcludingCommit < OffSiteMilliseconds,
            $"Off-site, {path}: p99 {row.P99ExcludingCommit} ms excluding the commit is not under {OffSiteMilliseconds} ms (NFR-03).");
    }
}
