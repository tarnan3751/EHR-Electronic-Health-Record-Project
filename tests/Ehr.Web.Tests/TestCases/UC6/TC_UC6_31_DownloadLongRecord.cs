using Ehr.Data.Patients;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RecordDownload;
using R4 = Hl7.Fhir.Model;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-31
// Purpose / scenario: A record longer than the copy's limit: each kind of entry is cut at 600, the newest kept, as the
//   clinician's FHIR API cuts a search, and the copy says what it left out, in the file as an OperationOutcome entry and on
//   the printable page in words. A result is never crowded out by vital signs, nor the other way round. The printable
//   page holds the newest 50 visit summaries at most, as Visit summaries does, and both pages say so.
// Type: Functional (alternative flow: a long record)
// Requirements: proposed FR-56, NFR-04
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a confirmed portal account, signed in, and 601 released results, one a day, and a heart
//   rate. Dr. Test, with the patient on their panel, has released a summary of each of 50 signed notes.
// Inputs and system conditions: GET /portal/record, /portal/record?handler=File and /portal/record/print, on the
//   on-site app.
// Test data: results "Synthetic glucose 1" (yesterday) to "Synthetic glucose 601" (601 days ago).
// Steps:
//   1. Download the file.
//   2. Open the printable page, and the page that offers it.
// Expected result: Step 1: a valid file with 600 results, "Synthetic glucose 1" among them and "Synthetic glucose 601"
//   not, the heart rate, and the warning "Only the newest 600 of the 601 test results are in this file.". Step 2: 600
//   results and "Only the newest 600 of the 601 test results are shown."; 50 summaries and "This page shows at most the
//   50 newest visit summaries.", which nothing says before there are 50; Download my record says the printable page has "the 50 newest visit summaries at most".
// Creator: Tahseen Arnan
public class TC_UC6_31_DownloadLongRecord(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_record_over_the_limit_keeps_the_newest_and_says_what_it_left_out()
    {
        var (_, session, patient) = await VisitSummaryTests.PortalPatientAsync(apps, "Longrecord");
        await FhirRequests.InsertAsync(apps,
            """
            INSERT INTO ehr.observations (id, patient_id, code, display, status, value, unit, reference_low, reference_high, effective_at, issued_at)
            SELECT CASE WHEN n = 1 THEN @id ELSE gen_random_uuid() END, @patient, '2345-7', 'Synthetic glucose ' || n, 'final', 90, 'mg/dL', 70, 99,
                now() - make_interval(days => n), now() - make_interval(days => n)
            FROM generate_series(1, 601) AS n
            """, patient.Patient);
        await apps.InsertVisitAsync(patient.Patient, DateTimeOffset.UtcNow.AddDays(-2), heartRate: 70);

        var file = await DownloadAsync(patient.Client);
        var bundle = Bundle(file);
        var observations = Entries<R4.Observation>(bundle);
        var results = observations.Where(o => o.Category.Any(c => c.Coding.Any(coding => coding.Code == "laboratory"))).Select(o => o.Code?.Text).ToList();
        Assert.Equal(600, results.Count);
        Assert.Contains("Synthetic glucose 1", results);
        Assert.DoesNotContain("Synthetic glucose 601", results);
        Assert.Contains(observations, o => o.Code?.Coding.Any(c => c.Code == VitalSign.HeartRate) == true);
        var issue = Assert.Single(Entries<R4.OperationOutcome>(bundle).SelectMany(outcome => outcome.Issue));
        Assert.Equal("Only the newest 600 of the 601 test results are in this file.", issue.Diagnostics);
        Assert.Equal(R4.OperationOutcome.IssueSeverity.Warning, issue.Severity);

        Assert.DoesNotContain("newest visit summaries", Words(await PrintAsync(patient.Client)));
        for (var i = 0; i < 50; i++)
        {
            var note = await NoteAddendumTests.SignedNoteAsync(session.Client, patient.Patient);
            using var released = await VisitSummaryTests.ReleaseAsync(session.Client, patient.Patient, note);
            Assert.Contains("Released.", Words(await ReadAsync(released)));
        }

        var page = await PrintAsync(patient.Client);
        Assert.Contains("Only the newest 600 of the 601 test results are shown.", Words(page));
        Assert.Equal(600, page.Split("Synthetic glucose ").Length - 1);
        Assert.Contains("This page shows at most the 50 newest visit summaries.", Words(page));
        Assert.Equal(50, page.Split("data-summary=\"standing\"").Length - 1);
        Assert.Contains("the 50 newest visit summaries at most", Words(await patient.Client.GetStringAsync(PagePath, TestContext.Current.CancellationToken)));
    }
}
