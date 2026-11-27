using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.RecordDownload;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-25
// Purpose / scenario: The patient opens their record as a printable page: the same record as the file, and the visit
//   summaries the portal's summaries page shows, laid out to print, in the portal's own formats. Opening it is a view,
//   recorded exactly as the summaries page records one.
// Type: Functional (successful flow: the printable page)
// Requirements: proposed FR-56, proposed FR-55
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: As TC-UC6-24, and Dr. Print, with the patient on their panel, has signed a note and released the test's
//   summary of it.
// Inputs and system conditions: GET /portal/summaries, then /portal/record/print, on the on-site app.
// Test data: FhirRequests.ChartAsync's record; the test's summary.
// Steps:
//   1. Open Visit summaries.
//   2. Open the printable page.
// Expected result: Step 2: Cache-Control no-store; headed "Health record of Synthetic Fhirchart" and "as of" its time in
//   UTC; the MRN in mono type and the date of birth as the portal's home shows it; each allergy (penicillin "High risk"),
//   the medication and diagnosis that aren't sensitive with status and date, each result with its value, range, flag,
//   date as the results page dates one, and the care team's note; the blood pressure as one reading "128/82 mmHg"; both
//   appointments, the cancelled one "Cancelled" with no reason; the summary with who wrote it and its three parts; the
//   notice that two sensitive items are hidden, with its link. It records what step 1 recorded: a read of the patient
//   and a read of the summary, by the patient's account, and no download.
// Creator: Tahseen Arnan
public class TC_UC6_25_PrintableRecord(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_patient_opens_their_record_laid_out_to_print_with_their_visit_summaries()
    {
        var (chart, patient) = await PortalChartAsync(apps, "Dr. Print");
        var note = await SignedNoteAsync(chart.Session.Client, chart.Patient);
        using (var released = await ReleaseAsync(chart.Session.Client, chart.Patient, note))
        {
            Assert.Contains("Released.", Words(await ReadAsync(released)));
        }

        var summaryPage = await ReadsAsync(() => patient.Client.GetStringAsync("/portal/summaries", Cancel), patient);
        using var response = await patient.Client.GetAsync(PrintPath, Cancel);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var html = await response.Content.ReadAsStringAsync(Cancel);
        var words = Words(html);

        Assert.Contains("<h1>Health record of Synthetic Fhirchart</h1>", html);
        Assert.Matches("A copy from the SwiftChart patient portal, as of [A-Z][a-z]{2} \\d{1,2}, \\d{4} \\d{2}:\\d{2} UTC\\.", words);
        Assert.Contains($"<dd class=\"mono-id\">{chart.Mrn}</dd>", html);
        Assert.Contains("<dd>1980-01-01</dd>", html);
        Assert.Contains("Penicillin G High risk", words);
        Assert.Contains("Hives", words);
        Assert.Contains("lisinopril 10 MG Oral Tablet", words);
        Assert.Contains("1 tablet by mouth daily", words);
        Assert.Contains("Jan 5, 2024", words);
        Assert.Contains("Essential (primary) hypertension (I10)", words);
        Assert.Contains("May 1, 2021", words);
        Assert.Contains("Hemoglobin A1c", words);
        Assert.Contains("6.9 % High", words);
        Assert.Contains("4 to 5.6 %", words);
        Assert.Contains("Note from the care team: Synthetic: we'll talk about this at your next visit.", words);
        Assert.Contains(chart.ResultsAt.UtcDateTime.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture) + " UTC", words);
        Assert.Contains("Blood pressure", words);
        Assert.Contains("128/82 mmHg", words);
        Assert.Contains("72 per minute", words);
        Assert.Contains("Synthetic: blood pressure check", words);
        Assert.Contains("Cancelled", words);
        Assert.DoesNotContain("asked to move it", words);
        Assert.Contains("Written by Dr. Print", words);
        Assert.Contains(TalkedAbout, words);
        Assert.Contains("<h4>What happens next</h4>", html);
        Assert.Contains("Sensitive items hidden. 2 diagnoses, medications or visit summaries are not shown. Show sensitive items", words);
        Assert.Contains("href=\"/portal/record/print?sensitive=true\"", html);
        Assert.DoesNotContain("sertraline", words);
        Assert.DoesNotContain("anxiety", words);

        // The same events as the summaries page: a read of the patient and a read of the summary, and no download.
        var printPage = await ReadsAsync(() => patient.Client.GetStringAsync(PrintPath, Cancel), patient);
        Assert.Equal(summaryPage, printPage);
        Assert.Equal(["read patients", "read visit_summaries"], printPage);
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.Download, patient.Account, patient.Patient));
    }

    // The events one request recorded under the patient's account: "action table", in order.
    async Task<List<string>> ReadsAsync(Func<Task<string>> request, ProxyTests.Person patient)
    {
        const string events = "SELECT action || ' ' || coalesce(table_name, '-') FROM ehr.audit_events WHERE patient_id = @patient AND actor_id = '{0}' ORDER BY occurred_at, table_name";
        var before = (await ProxyTests.QueryAsync(apps, string.Format(System.Globalization.CultureInfo.InvariantCulture, events, patient.Account), patient.Patient)).Count;
        await request();
        return [.. (await ProxyTests.QueryAsync(apps, string.Format(System.Globalization.CultureInfo.InvariantCulture, events, patient.Account), patient.Patient)).Skip(before).Order()];
    }
}
