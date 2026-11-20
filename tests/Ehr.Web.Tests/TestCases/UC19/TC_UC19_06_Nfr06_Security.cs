using System.Net;
using System.Text.RegularExpressions;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.FhirRequests;
using R4 = Hl7.Fhir.Model;

namespace Ehr.Web.Tests.TestCases.UC19;

// Test Case ID: TC-UC19-06
// Purpose / scenario: Only a clinician gets anything from the FHIR API, and only for a patient on their panel. Anyone
//   else is refused with a status and an OperationOutcome in fixed words, never sent to a sign-in page, and nothing is
//   read or recorded. A search by MRN doesn't tell a clinician whether a patient off their panel exists: it answers as
//   for an MRN that is nobody's. No answer is kept in a browser's or a proxy's cache, and none lets a page on another
//   site read it.
// Type: Non-functional (security: who may read, and what is kept)
// Requirements: NFR-06, with FR-29 and FR-32
// Use case: UC 19, Read a patient's record through the FHIR API
// Preconditions: As TC-UC19-01. An admin, signed in; the patient's own portal account, signed in; the desktop app's
//   session token of the patient's clinician; a second clinician without the patient on their panel, signed in.
// Inputs and system conditions: GET /fhir/metadata, GET /fhir/Patient/{id}, GET /fhir/Observation?patient={id}, GET
//   /fhir/Condition/{id} and GET /fhir/Patient?identifier={MRN}, as each caller, on the on-site app; the same without
//   signing in, with an Origin header of another site, and as an OPTIONS request.
// Test data: The patient of TC-UC19-01; Origin "https://elsewhere.example"; the MRN "SYN-NOT-AN-MRN", nobody's.
// Steps:
//   1. Without signing in, make each request, then again from another site's page, and as OPTIONS.
//   2. Make each request as the admin, as the portal account, with the desktop app's token, and as the clinician
//      without the patient on their panel; as that clinician, also search for the MRN that is nobody's.
//   3. Count the reads recorded about the patient, and those by the second clinician.
// Expected result: Step 1: 401, code login, with no Location header. Step 2: the admin 403, code forbidden ("This API
//   is for clinicians."); the portal account and the desktop token 401, since the API reads only the clinical app's
//   sign-in; the other clinician 403 for every read and search about the patient, metadata aside, but the search by the
//   patient's MRN, which is 200 with an empty searchset, the same, byte for byte, as the answer for the MRN that is
//   nobody's, but for the self link, which repeats what was asked. Every answer is application/fhir+json with
//   Cache-Control no-store and no Access-Control-* header, and no refusal holds the patient's name, MRN or ID. Step 3: no
//   read and no read of sensitive items, by anyone.
// Creator: Tahseen Arnan
public class TC_UC19_06_Nfr06_Security(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Only_a_clinician_with_the_patient_on_their_panel_is_answered_and_nothing_is_cached()
    {
        var chart = await ChartAsync(apps, "Dr. Fhir Owner");
        // The callers, signed in first: the portal's sign-in opens its home, which reads the patient's own record.
        var admin = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("A Fhir Admin", StaffRoles.Admin));
        var portal = await PortalResultsTests.SignInPatientAsync(apps, apps.OnPrem, chart.Patient);
        var token = await apps.DesktopSignInAsync(apps.OnPrem, chart.Clinician);
        var colleague = await apps.CreateStaffAsync("Dr. Fhir Elsewhere", StaffRoles.Clinician);
        var elsewhere = await SignInAsync(apps.OnPrem, colleague);
        var colleagueReads = await apps.AuditEventsAsync(AuditActions.Read, colleague.Id);
        var reads = await apps.AuditEventsAsync(AuditActions.Read, patientId: chart.Patient);
        var sensitive = await apps.AuditEventsAsync(AuditActions.ReadSensitive, patientId: chart.Patient);
        string[] aboutThePatient =
        [
            $"/fhir/Patient/{chart.Patient}", $"/fhir/Observation?patient={chart.Patient}", $"/fhir/Condition/{chart.Hypertension}",
            $"/fhir/Patient?identifier={chart.Mrn}",
        ];

        void NothingOfThePatient(Answer answer)
        {
            Assert.DoesNotContain(chart.Patient.ToString(), answer.Json);
            Assert.DoesNotContain(chart.Mrn, answer.Json);
            Assert.DoesNotContain("Fhirchart", answer.Json);
        }

        // 1. Not signed in, from here or another site's page, and as a preflight.
        var anonymous = CreateClient(apps.OnPrem);
        foreach (var path in aboutThePatient.Prepend("/fhir/metadata"))
        {
            foreach (var answer in new[]
            {
                await GetAsync(anonymous, path),
                await GetAsync(anonymous, path, ("Origin", "https://elsewhere.example")),
                await SendAsync(anonymous, HttpMethod.Options, path, ("Origin", "https://elsewhere.example"), ("Access-Control-Request-Method", "GET")),
            })
            {
                Refused(answer, HttpStatusCode.Unauthorized, "login");
                NothingOfThePatient(answer);
            }
        }

        // 2. Signed in, but not as a clinician with the patient on their panel.
        foreach (var path in aboutThePatient.Prepend("/fhir/metadata"))
        {
            var asAdmin = await GetAsync(admin.Client, path, ("Origin", "https://elsewhere.example"));
            Assert.Equal("This API is for clinicians.", Refused(asAdmin, HttpStatusCode.Forbidden, "forbidden").Issue.Single().Diagnostics);
            Refused(await GetAsync(portal.Client, path), HttpStatusCode.Unauthorized, "login");
            Refused(await GetAsync(CreateClient(apps.OnPrem), path, ("Authorization", $"Bearer {token}")), HttpStatusCode.Unauthorized, "login");
            NothingOfThePatient(asAdmin);
        }

        Expect(await GetAsync(elsewhere.Client, "/fhir/metadata"), HttpStatusCode.OK);
        foreach (var path in aboutThePatient.Where(path => !path.Contains("identifier=", StringComparison.Ordinal)))
        {
            var refused = await GetAsync(elsewhere.Client, path, ("Origin", "https://elsewhere.example"));
            Assert.StartsWith("This patient isn't on your panel.", Refused(refused, HttpStatusCode.Forbidden, "forbidden").Issue.Single().Diagnostics);
            NothingOfThePatient(refused);
        }

        // The search by MRN: the patient's and one that is nobody's get the same answer, but for the self link.
        var known = await GetAsync(elsewhere.Client, $"/fhir/Patient?identifier={chart.Mrn}", ("Origin", "https://elsewhere.example"));
        var nobodys = await GetAsync(elsewhere.Client, "/fhir/Patient?identifier=SYN-NOT-AN-MRN", ("Origin", "https://elsewhere.example"));
        foreach (var answer in new[] { known, nobodys })
        {
            var (bundle, matches, notes) = Searched<R4.Patient>(answer);
            Assert.Equal((0, 0, 0), (bundle.Total, matches.Count, notes.Count));
        }

        // The Bundle's links, and the comma before them, cut out of its JSON as it was sent.
        static string WithoutLinks(string json) => Regex.Replace(json, ",\"link\":\\[[^\\]]*\\]", "");
        Assert.NotEqual(known.Json, WithoutLinks(known.Json));
        Assert.Equal(WithoutLinks(nobodys.Json), WithoutLinks(known.Json));
        Assert.Equal(nobodys.Headers.Order(StringComparer.Ordinal), known.Headers.Order(StringComparer.Ordinal));

        // 3. Nothing was read.
        Assert.Equal(colleagueReads, await apps.AuditEventsAsync(AuditActions.Read, colleague.Id));
        Assert.Equal(reads, await apps.AuditEventsAsync(AuditActions.Read, patientId: chart.Patient));
        Assert.Equal(sensitive, await apps.AuditEventsAsync(AuditActions.ReadSensitive, patientId: chart.Patient));
    }
}
