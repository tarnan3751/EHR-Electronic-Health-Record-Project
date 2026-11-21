using System.Net;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.RecordDownload;
using R4 = Hl7.Fhir.Model;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-24
// Purpose / scenario: The patient downloads their record from the portal as a FHIR file: from the home page's link to
//   "Download my record", which says what the copy holds and that it is theirs to look after once downloaded, then
//   "Download as a file (FHIR)". The file is one FHIR R4 collection Bundle of their record, from the same mapping as the
//   clinician's FHIR API, valid against R4 and the vital-sign profiles it claims, saved rather than shown, never cached.
// Type: Functional (successful flow: the file)
// Requirements: proposed FR-56
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a confirmed portal account and is signed in on the on-site app. Their record holds
//   two allergies, two medications (one marked sensitive), two diagnoses (one sensitive by its code), two released
//   results, a set of vital signs taken through the vital signs page, and two appointments, one cancelled with a reason.
// Inputs and system conditions: GET /portal, /portal/record, then /portal/record?handler=File, on the on-site app.
// Test data: FhirRequests.ChartAsync's record; the appointment's cancellation reason "Synthetic: the patient asked to
//   move it".
// Steps:
//   1. Open the portal's home and follow "Download my record".
//   2. Select "Download as a file (FHIR)".
// Expected result: Step 1: the page says what the copy holds and, before anything is downloaded, "Health information:"
//   and that where it goes is the patient's responsibility; it offers "Include sensitive items"; opening it records
//   nothing. Step 2: 200, application/fhir+json, Content-Disposition attachment named health-record-<today, UTC>.json,
//   Cache-Control no-store, no CORS header; a valid collection Bundle: the patient with their MRN; both allergies; the
//   medication and diagnosis that aren't sensitive; both results; the vital signs, the blood pressure as one Observation
//   with two components; both appointments, without the cancellation reason, the clinician named and not referred to;
//   every reference to the patient at the patient's entry. One recorded download of the patient by the patient's account,
//   and no other event.
// Creator: Tahseen Arnan
public class TC_UC6_24_DownloadRecordFile(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_patient_downloads_their_record_as_one_valid_fhir_file()
    {
        var (chart, patient) = await PortalChartAsync(apps, "Dr. Download");

        var home = await patient.Client.GetStringAsync("/portal", Cancel);
        Assert.Contains("href=\"/portal/record\"", home);
        Assert.Contains("Download my record", home);
        var events = await AllEventsAsync(patient.Account);
        using (var page = await patient.Client.GetAsync(PagePath, Cancel))
        {
            var html = await page.Content.ReadAsStringAsync(Cancel);
            var words = Browser.Words(html);
            Assert.True(page.Headers.CacheControl?.NoStore);
            Assert.Contains("<h1>Download my record</h1>", html);
            Assert.Contains("Health information: the file and the printable page hold your health information.", words);
            Assert.Contains("where they go is your responsibility", words);
            Assert.Contains("name=\"sensitive\"", html);
            Assert.Contains("name=\"handler\" value=\"File\"", html);
            Assert.Contains("formaction=\"/portal/record/print\"", html);
        }

        Assert.Equal(events, await AllEventsAsync(patient.Account));

        var file = await DownloadAsync(patient.Client);
        Assert.Equal(FhirRequests.ContentType, file.ContentType);
        Assert.Equal("attachment", file.Disposition);
        Assert.Equal($"health-record-{DateTime.UtcNow:yyyy-MM-dd}.json", file.FileName);
        Assert.Contains("no-store", (file.CacheControl ?? "").Split(", "));
        Assert.DoesNotContain(file.Headers, header => header.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
        var bundle = Bundle(file);

        var own = Assert.Single(Entries<R4.Patient>(bundle));
        Assert.Equal("Fhirchart", own.Name.Single().Family);
        Assert.Equal(chart.Mrn, own.Identifier.Single(identifier => identifier.System == "http://swiftchart.example/fhir/sid/mrn").Value);
        var self = $"urn:uuid:{own.Id}";
        Assert.Equal(["Peanut", "Penicillin G"], Entries<R4.AllergyIntolerance>(bundle).Select(a => a.Code?.Text).Order());
        Assert.Equal(["lisinopril 10 MG Oral Tablet"], Entries<R4.MedicationStatement>(bundle).Select(m => m.Medication is R4.CodeableConcept c ? c.Text : ""));
        Assert.Equal(["Essential (primary) hypertension"], Entries<R4.Condition>(bundle).Select(c => c.Code?.Text));
        var observations = Entries<R4.Observation>(bundle);
        Assert.Equal(["Hemoglobin A1c", "Thyroid stimulating hormone"], observations.Where(IsLaboratory).Select(o => o.Code.Text).Order());
        // Eight vital signs: the blood pressure reading as one, and the seven others.
        Assert.Equal(8, observations.Count(o => !IsLaboratory(o)));
        var pressure = Assert.Single(observations, o => o.Code.Coding.Any(c => c.Code == "85354-9"));
        Assert.Equal(["8480-6", "8462-4"], pressure.Component.Select(c => c.Code.Coding.Single().Code));
        Assert.Contains("http://hl7.org/fhir/StructureDefinition/bp", pressure.Meta?.Profile ?? []);
        var appointments = Entries<R4.Appointment>(bundle);
        Assert.Equal(2, appointments.Count);
        Assert.All(appointments, a => Assert.Null(a.CancelationReason));
        Assert.All(appointments, a => Assert.Equal([self, null], a.Participant.Select(p => p.Actor?.Reference)));
        Assert.All(appointments, a => Assert.Equal("Dr. Download", a.Participant[1].Actor?.Display));
        Assert.DoesNotContain("asked to move it", file.Body);
        Assert.All(Entries<R4.AllergyIntolerance>(bundle), a => Assert.Equal(self, a.Patient.Reference));
        Assert.All(Entries<R4.MedicationStatement>(bundle), m => Assert.Equal(self, m.Subject.Reference));
        Assert.All(Entries<R4.Condition>(bundle), c => Assert.Equal(self, c.Subject.Reference));
        Assert.All(observations, o => Assert.Equal(self, o.Subject?.Reference));
        Assert.Empty(Notes(bundle));

        // One download of the patient, and nothing else.
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Download, patient.Account, patient.Patient));
        Assert.Equal(events + 1, await AllEventsAsync(patient.Account));
    }

    static bool IsLaboratory(R4.Observation observation) => observation.Category.Any(c => c.Coding.Any(coding => coding.Code == "laboratory"));

    async Task<long> AllEventsAsync(Guid account) =>
        long.Parse((await ProxyTests.QueryAsync(apps, $"SELECT count(*)::text FROM ehr.audit_events WHERE actor_id = '{account}' AND @patient IS NOT NULL", account)).Single(),
            System.Globalization.CultureInfo.InvariantCulture);
}
