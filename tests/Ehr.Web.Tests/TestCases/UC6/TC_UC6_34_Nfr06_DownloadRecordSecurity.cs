using Xunit;
using static Ehr.Web.Tests.RecordDownload;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-34
// Purpose / scenario: The copy carries no identifier of the clinic's: the file's ids and references are made for that
//   file alone, none of them the ID of anything in the database, and two downloads share none; the printable page's
//   markup has no ID at all; the patient's own record number is the one number kept. None of the three answers is ever
//   cached, or readable by a page on another site, and the file's name says nothing about whose it is.
// Type: Non-functional (security)
// Requirements: NFR-06, proposed FR-56
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: As TC-UC6-24, with sensitive items asked for.
// Inputs and system conditions: GET /portal/record, /portal/record?handler=File&sensitive=true (twice) and
//   /portal/record/print?sensitive=true, on the on-site app.
// Test data: FhirRequests.ChartAsync's record.
// Steps:
//   1. Download the file twice, and read every UUID in each.
//   2. Read the database's IDs for the patient, their allergies, medications, diagnoses, results, vital signs and their
//      readings, appointments, and the clinician.
//   3. Open the page and the printable page.
// Expected result: Step 1: each UUID in a file is an id or urn:uuid: address of its own, version 4; the two files share
//   none. Steps 1 and 2: no UUID in either file is any of the database's; neither names a Practitioner. Step 3: no UUID
//   in either page's markup, and no ID in another form in its content (the app bar shows the account's own address);
//   the MRN is there. All three answers: Cache-Control no-store and no Access-Control
//   header; the file's name is health-record-<date>.json, with no name, MRN or ID.
// Creator: Tahseen Arnan
public class TC_UC6_34_Nfr06_DownloadRecordSecurity(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_copy_carries_no_identifier_of_the_clinics_and_is_never_cached()
    {
        var (chart, patient) = await PortalChartAsync(apps, "Dr. Security");

        var first = await DownloadAsync(patient.Client, sensitive: true);
        var second = await DownloadAsync(patient.Client, sensitive: true);
        var made = Uuid().Matches(first.Body).Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var again = Uuid().Matches(second.Body).Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(made);
        Assert.All(made, id => Assert.Equal(4, Guid.Parse(id).Version));
        Assert.Empty(made.Intersect(again));

        var records = await ProxyTests.QueryAsync(apps,
            $"""
            SELECT id::text FROM ehr.patients WHERE id = @patient
            UNION ALL SELECT id::text FROM ehr.allergy_intolerances WHERE patient_id = @patient
            UNION ALL SELECT id::text FROM ehr.medication_statements WHERE patient_id = @patient
            UNION ALL SELECT id::text FROM ehr.conditions WHERE patient_id = @patient
            UNION ALL SELECT id::text FROM ehr.observations WHERE patient_id = @patient
            UNION ALL SELECT reading_id::text FROM ehr.observations WHERE patient_id = @patient AND reading_id IS NOT NULL
            UNION ALL SELECT id::text FROM ehr.appointments WHERE patient_id = @patient
            UNION ALL SELECT id::text FROM ehr.encounters WHERE patient_id = @patient
            UNION ALL SELECT id::text FROM ehr.staff_users WHERE id = '{chart.Clinician.Id}'
            UNION ALL SELECT id::text FROM ehr.patient_users WHERE patient_id = @patient
            """, patient.Patient);
        Assert.True(records.Count >= 18, $"Expected the record's IDs, found {records.Count}.");
        Assert.Empty(made.Union(again).Intersect(records, StringComparer.OrdinalIgnoreCase));
        foreach (var file in new[] { first, second })
        {
            Assert.DoesNotContain("Practitioner", file.Body);
            Assert.Contains(chart.Mrn, file.Body);
            Assert.Matches("^health-record-\\d{4}-\\d{2}-\\d{2}\\.json$", file.FileName ?? "");
            Assert.Contains("no-store", (file.CacheControl ?? "").Split(", "));
            Assert.DoesNotContain(file.Headers, header => header.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
        }

        foreach (var path in new[] { PagePath, PrintPath + "?sensitive=true" })
        {
            using var response = await patient.Client.GetAsync(path, Cancel);
            var html = await response.Content.ReadAsStringAsync(Cancel);
            // The page's own content: the app bar shows the account's own address, which is the account's, not an ID.
            var main = html[html.IndexOf("<main", StringComparison.Ordinal)..html.IndexOf("</main>", StringComparison.Ordinal)];
            Assert.True(response.Headers.CacheControl?.NoStore, path);
            Assert.DoesNotContain(response.Headers, header => header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(Uuid().Matches(html));
            Assert.DoesNotMatch("[0-9a-fA-F]{32}", main);
        }

        Assert.Contains(chart.Mrn, await PrintAsync(patient.Client));
    }
}
