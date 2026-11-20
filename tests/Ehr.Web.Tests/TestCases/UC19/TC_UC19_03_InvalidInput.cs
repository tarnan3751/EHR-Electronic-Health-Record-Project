using System.Net;
using Xunit;
using static Ehr.Web.Tests.FhirRequests;
using R4 = Hl7.Fhir.Model;

namespace Ehr.Web.Tests.TestCases.UC19;

// Test Case ID: TC-UC19-03
// Purpose / scenario: A request the API can't answer gets a FHIR OperationOutcome saying why, in fixed words, with
//   nothing from the request or the record in it, and reads nothing: a type it doesn't serve, an ID that is no
//   resource, a search without its patient, a parameter it doesn't take (at /fhir/metadata too), a value it can't use,
//   such as an MRN no MRN could be, an answer in anything but JSON, and any write.
// Type: Functional (invalid input)
// Requirements: proposed FR-53
// Use case: UC 19, Read a patient's record through the FHIR API
// Preconditions: As TC-UC19-01: the clinician is signed in on the on-site app, with the patient on their panel.
// Inputs and system conditions: The requests below, on the on-site app.
// Test data: The patient of TC-UC19-01; a random ID; "Synthetic-not-an-id"; the category "imaging"; _format=xml;
//   _count=10; an MRN with a NUL in it, and one of 21 characters; the Accept headers below.
// Steps:
//   1. GET a type that isn't served, an ID that isn't one, an unknown ID, and addresses that aren't the API's.
//   2. GET searches without their patient or MRN, with an unknown, repeated or unusable parameter; and the capability
//      statement with a parameter it doesn't take.
//   3. POST, PUT, PATCH and DELETE on a type, on a resource, on /fhir itself and on an operation; and HEAD and OPTIONS;
//      then POST, PUT and DELETE accepting only FHIR XML.
//   4. GET a search and the capability statement accepting only FHIR XML; then a search with each Accept header below,
//      and with _format=json beside XML only.
//   5. Count the reads recorded.
// Expected result: Step 1: 404, code not-supported for the type, not-found for the rest. Step 2: 400, code required for
//   a missing patient or MRN, not-supported for a parameter not taken, invalid for a value, an MRN with a control
//   character or longer than 20 characters among them; _format=json is taken. Step 3: 405 with Allow: GET, code
//   not-supported, whatever the request accepts. Step 4: 406, code not-supported, for XML only; for the rest, as RFC
//   9110's rule gives it (the most specific range matching application/fhir+json decides, by its q, and application/json
//   is taken as asking for it unless application/fhir+json is named): 406 for "application/fhir+json;q=0",
//   "application/fhir+json;q=0, */*;q=0.1", "application/*;q=0, */*", "application/json;q=0" and
//   "application/fhir+json;q=0, application/json"; the answer, in JSON, for "application/fhir+xml,
//   application/fhir+json;q=0.5", "application/fhir+xml, */*;q=0.1", "application/json", "application/json;q=0, */*",
//   a browser's usual header, and _format=json. Each refusal is a valid OperationOutcome, application/fhir+json,
//   no-store, with none of the request's values or the patient's details in it. Step 5: none but the searches that
//   were answered.
// Creator: Tahseen Arnan
public class TC_UC19_03_InvalidInput(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Each_request_the_api_cannot_answer_gets_an_operation_outcome_and_reads_nothing()
    {
        var chart = await ChartAsync(apps, "Dr. Fhir Mistyped");
        var client = chart.Session.Client;
        var patient = chart.Patient;
        var before = await ReadsAsync(apps, chart.Clinician.Id, patient);
        var unknown = Guid.CreateVersion7();

        (string Path, HttpStatusCode Status, string Code)[] gets =
        [
            // 1.
            ($"/fhir/Practitioner/{chart.Clinician.Id}", HttpStatusCode.NotFound, "not-supported"),
            ($"/fhir/Encounter?patient={patient}", HttpStatusCode.NotFound, "not-supported"),
            ("/fhir/condition?patient=" + patient, HttpStatusCode.NotFound, "not-supported"),
            ("/fhir/Condition/Synthetic-not-an-id", HttpStatusCode.NotFound, "not-found"),
            ($"/fhir/Condition/{unknown}", HttpStatusCode.NotFound, "not-found"),
            ($"/fhir/Observation/{chart.Hypertension}", HttpStatusCode.NotFound, "not-found"),
            ($"/fhir/Patient/{patient}/_history", HttpStatusCode.NotFound, "not-found"),
            ("/fhir", HttpStatusCode.NotFound, "not-found"),
            // 2.
            ("/fhir/Condition", HttpStatusCode.BadRequest, "required"),
            ("/fhir/Condition?patient=Synthetic-not-an-id", HttpStatusCode.BadRequest, "invalid"),
            ("/fhir/Patient?identifier=a|b|c", HttpStatusCode.BadRequest, "invalid"),
            // No MRN has a control character or more than 20 characters: refused before the database is asked, which
            // would refuse a NUL in text.
            ("/fhir/Patient?identifier=SYN%00", HttpStatusCode.BadRequest, "invalid"),
            ($"/fhir/Patient?identifier={new string('7', 21)}", HttpStatusCode.BadRequest, "invalid"),
            ($"/fhir/Patient?identifier={Uri.EscapeDataString($"http://swiftchart.example/fhir/sid/mrn|{new string('7', 21)}")}", HttpStatusCode.BadRequest, "invalid"),
            ("/fhir/Patient", HttpStatusCode.BadRequest, "required"),
            ($"/fhir/Patient?name=Fhirchart", HttpStatusCode.BadRequest, "not-supported"),
            ($"/fhir/Condition?patient={patient}&_count=10", HttpStatusCode.BadRequest, "not-supported"),
            ($"/fhir/Condition?subject=Patient/{patient}", HttpStatusCode.BadRequest, "not-supported"),
            ($"/fhir/Condition/{chart.Hypertension}?patient={patient}", HttpStatusCode.BadRequest, "not-supported"),
            ($"/fhir/AllergyIntolerance?patient={patient}&show-sensitive=true", HttpStatusCode.BadRequest, "not-supported"),
            ($"/fhir/Condition?patient={patient}&patient={patient}", HttpStatusCode.BadRequest, "invalid"),
            ($"/fhir/Condition?patient={patient}&show-sensitive=yes", HttpStatusCode.BadRequest, "invalid"),
            ($"/fhir/Observation?patient={patient}&category=imaging", HttpStatusCode.BadRequest, "invalid"),
            ($"/fhir/Condition?patient={patient}&_format=xml", HttpStatusCode.BadRequest, "not-supported"),
            ("/fhir/metadata?mode=full", HttpStatusCode.BadRequest, "not-supported"),
            ("/fhir/metadata?_format=xml", HttpStatusCode.BadRequest, "not-supported"),
        ];
        foreach (var (path, status, code) in gets)
        {
            var answer = await GetAsync(client, path);
            Refused(answer, status, code);
            Assert.DoesNotContain(patient.ToString(), answer.Json);
            Assert.DoesNotContain(chart.Mrn, answer.Json);
            Assert.DoesNotContain("Fhirchart", answer.Json);
            Assert.DoesNotContain("imaging", answer.Json);
            Assert.DoesNotContain("Synthetic-not-an-id", answer.Json);
            Assert.DoesNotContain("777777", answer.Json);
        }

        // 3. Writes, and every other method.
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            foreach (var path in new[] { "/fhir/Patient", $"/fhir/Patient/{patient}", "/fhir", "/fhir/Patient/$everything", $"/fhir/Condition?patient={patient}" })
            {
                var refused = await SendAsync(client, method, path);
                Refused(refused, HttpStatusCode.MethodNotAllowed, "not-supported");
                Assert.Equal("GET", refused.Allow);
            }
        }

        foreach (var method in new[] { HttpMethod.Head, HttpMethod.Options })
        {
            var refused = await SendAsync(client, method, $"/fhir/Patient/{patient}");
            Assert.Equal(HttpStatusCode.MethodNotAllowed, refused.Status);
            Assert.Equal("GET", refused.Allow);
        }

        // A write is 405 first, whatever it accepts: never the 406 a GET accepting only XML gets.
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
        {
            var refused = await SendAsync(client, method, $"/fhir/Patient/{patient}", ("Accept", "application/fhir+xml"));
            Refused(refused, HttpStatusCode.MethodNotAllowed, "not-supported");
            Assert.Equal("GET", refused.Allow);
        }

        // _format is taken when it asks for JSON, at the capability statement too.
        Searched<R4.Condition>(await GetAsync(client, $"/fhir/Condition?patient={patient}&_format=json"));
        Searched<R4.Condition>(await GetAsync(client, $"/fhir/Condition?patient={patient}&_format={Uri.EscapeDataString("application/fhir+json")}"));
        FhirConformance.Valid<R4.CapabilityStatement>(Expect(await GetAsync(client, "/fhir/metadata?_format=json"), HttpStatusCode.OK).Json);

        // 4. Only XML acceptable: 406, before anything is read. _format decides when it is given; JSON after XML is taken.
        foreach (var path in new[] { $"/fhir/Condition?patient={patient}", "/fhir/metadata" })
        {
            Refused(await GetAsync(client, path, ("Accept", "application/fhir+xml")), HttpStatusCode.NotAcceptable, "not-supported");
        }

        // The most specific range that matches application/fhir+json decides, by its q (RFC 9110, 12.5.1).
        foreach (var refusing in new[]
        {
            "application/fhir+json;q=0", "application/fhir+json;q=0, */*;q=0.1", "application/*;q=0, */*", "application/json;q=0",
            "application/fhir+json;q=0, application/json",
        })
        {
            Refused(await GetAsync(client, $"/fhir/Condition?patient={patient}", ("Accept", refusing)), HttpStatusCode.NotAcceptable, "not-supported");
        }

        foreach (var taking in new[]
        {
            "application/fhir+xml, application/fhir+json;q=0.5", "application/fhir+xml, */*;q=0.1", "application/json", "application/json;q=0, */*",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
        })
        {
            Searched<R4.Condition>(await GetAsync(client, $"/fhir/Condition?patient={patient}", ("Accept", taking)));
        }

        Searched<R4.Condition>(await GetAsync(client, $"/fhir/Condition?patient={patient}&_format=json", ("Accept", "application/fhir+xml")));

        // 5. Nothing was read but the eight searches that were answered.
        Assert.Equal((before.Reads + 8, before.Sensitive), await ReadsAsync(apps, chart.Clinician.Id, patient));
    }
}
