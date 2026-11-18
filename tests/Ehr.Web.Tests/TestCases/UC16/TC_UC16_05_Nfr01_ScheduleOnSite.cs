using Xunit;

namespace Ehr.Web.Tests.TestCases.UC16;

// Test Case ID: TC-UC16-05
// Purpose / scenario: On-site, a clinician's day, the booking form and an appointment's page each take under 15 ms of
//   server time at the 99th percentile.
// Type: Non-functional (performance)
// Requirements: NFR-01, NFR-04
// Use case: UC 16, Schedule an appointment
// Preconditions: A run of the render benchmark (bench/Ehr.Bench) on a machine doing nothing else: its own PostgreSQL
//   18 with durability on, seeded with 50,000 synthetic patients; the on-site app in memory; a clinician signed in
//   with 100 patients on their panel and a day of 24 appointments, each with a different patient.
// Inputs and system conditions: GET /schedule?day=..., GET /patients/{id}/appointments/new and GET
//   /appointments/{id}, each 100 times to warm up and then 1,000 times.
// Test data: The synthetic clinic (bench/Ehr.Seed) and the benchmark's own appointments.
// Steps:
//   1. Run the benchmark with --results, as PerformanceResults.cs says.
//   2. Run this test case against that file.
// Expected result: The p99 of the whole request, the commit of its audit events included, is under 15 ms for each of
//   the rows "Schedule, a day of 24 appointments", "Book appointment form, a patient on the panel" and "Appointment, a
//   patient on the panel". Acceptance criterion: p99 < 15 ms, each.
// Creator: Tahseen Arnan
public class TC_UC16_05_Nfr01_ScheduleOnSite
{
    const double LimitMilliseconds = 15;

    [Theory]
    [Trait("Category", PerformanceResults.Category)]
    [InlineData("Schedule, a day of 24 appointments")]
    [InlineData("Book appointment form, a patient on the panel")]
    [InlineData("Appointment, a patient on the panel")]
    public void On_site_the_schedule_pages_render_in_under_15_ms_at_p99(string path)
    {
        var row = PerformanceResults.Load().Row(path);

        Assert.True(row.P99 < LimitMilliseconds, $"{path}: p99 {row.P99} ms, the commit included, is not under {LimitMilliseconds} ms (NFR-01).");
    }
}
