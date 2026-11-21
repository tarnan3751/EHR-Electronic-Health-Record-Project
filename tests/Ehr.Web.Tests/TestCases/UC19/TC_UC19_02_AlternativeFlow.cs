using System.Net;
using Ehr.Data.Accounts;
using Ehr.Data.Patients;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.FhirRequests;
using R4 = Hl7.Fhir.Model;

namespace Ehr.Web.Tests.TestCases.UC19;

// Test Case ID: TC-UC19-02
// Purpose / scenario: The other ways a read goes. Sensitive diagnoses and medications are left out until asked for with
//   show-sensitive=true, which is recorded as a view of sensitive items and marks them restricted. Emergency access
//   started in the clinical app opens the API as the panel does. A vital sign marked entered in error is left out, and
//   so is a blood pressure reading marked in error, whole. A patient whose only vital signs are blood pressures gets one
//   Observation for each reading. A search with more matches than the limit says so.
// Type: Functional (alternative flow)
// Requirements: proposed FR-53, with FR-06, FR-32, FR-39 (proposed) and FR-50 (proposed)
// Use case: UC 19, Read a patient's record through the FHIR API
// Preconditions: As TC-UC19-01. A second clinician without the patient on their panel. A second patient on the first
//   clinician's panel with 601 glucose results. A third with two blood pressure readings and no other vital sign.
// Inputs and system conditions: GET /fhir/Condition and /fhir/MedicationStatement, searched and read, with and
//   without show-sensitive=true; POST /patients/{id}/reason in the clinical app; POST /patients/{id}/vitals/error;
//   GET /fhir/Observation, searched with and without a category, and read; on the on-site app.
// Test data: Generalized anxiety disorder (F41.1, sensitive by its code) and sertraline (marked sensitive); the reason
//   "Synthetic: covering for a colleague today"; the heart rate and the blood pressure reading marked in error with
//   "Synthetic: taken from the wrong patient"; 601 results of glucose (LOINC 2345-7); readings of 131/85 and 122/79
//   mmHg, an hour apart.
// Steps:
//   1. Search diagnoses and medications, then read the sensitive ones by ID; then the same with show-sensitive=true.
//   2. As the second clinician, search the diagnoses; give a reason in the clinical app; search again.
//   3. Search the vital signs; mark the heart rate and the blood pressure's systolic in error; search the vital signs
//      again, with each form of the category, and the results; read the heart rate, the reading, the systolic and the
//      diastolic.
//   4. Search the second patient's results.
//   5. Search the third patient's observations, and read each reading by its ID.
// Expected result: Step 1: without the parameter each search leaves the sensitive entry out and says "1 sensitive entry
//   was left out", and the read by ID is not found; each answered search is one read of the patient. With it, each is
//   included with meta.security R (v3-Confidentiality), the other entries without; each request is one read of
//   sensitive items and no plain read. Step 2: 403, then the diagnoses. Step 3: the reading at first; after the marks,
//   both of the reading's values in error, and no heart rate and no reading in the search, which says nothing besides
//   its entries; each read is not found. Step 4: total 601, the newest 600, and a warning that only those are included.
//   Step 5: total 2, two Observations of LOINC 85354-9 claiming the bp profile, the newer first (122/79), each read by
//   its reading's ID; one recorded read per request. Every answer is valid R4.
// Creator: Tahseen Arnan
public class TC_UC19_02_AlternativeFlow(WebApps apps) : IClassFixture<WebApps>
{
    const string Restricted = "http://terminology.hl7.org/CodeSystem/v3-Confidentiality|R";

    static string Security(R4.Resource resource) => string.Join(",", resource.Meta?.Security.Select(s => $"{s.System}|{s.Code}") ?? []);

    [Fact]
    public async Task Sensitive_items_are_left_out_until_asked_for_and_asking_is_recorded()
    {
        var chart = await ChartAsync(apps, "Dr. Fhir Sensitive");
        var client = chart.Session.Client;
        var before = await ReadsAsync(apps, chart.Clinician.Id, chart.Patient);

        // 1. Without asking.
        var (_, conditions, notes) = Searched<R4.Condition>(await GetAsync(client, $"/fhir/Condition?patient={chart.Patient}"));
        Assert.Equal(["I10"], conditions.Select(c => c.Code!.Coding.Single().Code));
        Assert.Contains("1 sensitive entry was left out. Add show-sensitive=true to include them; asking is recorded.", notes);
        var (_, medications, _) = Searched<R4.MedicationStatement>(await GetAsync(client, $"/fhir/MedicationStatement?patient={chart.Patient}"));
        Assert.Equal([chart.Lisinopril.ToString()], medications.Select(m => m.Id));
        Refused(await GetAsync(client, $"/fhir/Condition/{chart.Anxiety}"), HttpStatusCode.NotFound, "not-found");
        Refused(await GetAsync(client, $"/fhir/MedicationStatement/{chart.Sertraline}"), HttpStatusCode.NotFound, "not-found");
        Assert.Equal((before.Reads + 2, before.Sensitive), await ReadsAsync(apps, chart.Clinician.Id, chart.Patient));

        // Asking: included and marked restricted, and each request a view of sensitive items.
        var (_, shown, shownNotes) = Searched<R4.Condition>(await GetAsync(client, $"/fhir/Condition?patient={chart.Patient}&show-sensitive=true"));
        Assert.Equal([$"F41.1 {Restricted}", "I10 "], shown.Select(c => $"{c.Code!.Coding.Single().Code} {Security(c)}").Order(StringComparer.Ordinal));
        Assert.Empty(shownNotes);
        var anxiety = FhirConformance.Valid<R4.Condition>(Expect(await GetAsync(client, $"/fhir/Condition/{chart.Anxiety}?show-sensitive=true"), HttpStatusCode.OK).Json);
        Assert.Equal(Restricted, Security(anxiety));
        var (_, allMedications, _) = Searched<R4.MedicationStatement>(await GetAsync(client, $"/fhir/MedicationStatement?patient={chart.Patient}&show-sensitive=true"));
        Assert.Equal(Restricted, Security(Assert.Single(allMedications, m => m.Id == chart.Sertraline.ToString())));
        var sertraline = FhirConformance.Valid<R4.MedicationStatement>(
            Expect(await GetAsync(client, $"/fhir/MedicationStatement/{chart.Sertraline}?show-sensitive=true"), HttpStatusCode.OK).Json);
        Assert.Equal(Restricted, Security(sertraline));
        // Saying no is the same as not asking.
        Searched<R4.Condition>(await GetAsync(client, $"/fhir/Condition?patient={chart.Patient}&show-sensitive=false"));
        Assert.Equal((before.Reads + 3, before.Sensitive + 4), await ReadsAsync(apps, chart.Clinician.Id, chart.Patient));
    }

    [Fact]
    public async Task Emergency_access_opens_the_api_and_entries_in_error_are_left_out_a_blood_pressure_reading_whole()
    {
        var chart = await ChartAsync(apps, "Dr. Fhir Panel");

        // 2. Off the panel, then under emergency access started in the clinical app.
        var colleague = await apps.CreateStaffAsync("Dr. Fhir Covering", StaffRoles.Clinician);
        var covering = await SignInAsync(apps.OnPrem, colleague);
        Refused(await GetAsync(covering.Client, $"/fhir/Condition?patient={chart.Patient}"), HttpStatusCode.Forbidden, "forbidden");
        using (var reason = await PostFormAsync(covering.Client, $"/patients/{chart.Patient}/reason", $"/patients/{chart.Patient}/reason",
            ("Reason", "Synthetic: covering for a colleague today")))
        {
            Assert.Equal($"/patients/{chart.Patient}", PathOf(reason));
        }

        Assert.Single(Searched<R4.Condition>(await GetAsync(covering.Client, $"/fhir/Condition?patient={chart.Patient}")).Matches);
        Expect(await GetAsync(covering.Client, $"/fhir/Patient/{chart.Patient}"), HttpStatusCode.OK);

        // 3. The blood pressure is served, as its reading; then a heart rate and the reading in error. Marking the
        // reading's systolic marks the reading, both values (step 9c).
        Assert.Contains(Searched<R4.Observation>(await GetAsync(chart.Session.Client, $"/fhir/Observation?patient={chart.Patient}&category=vital-signs")).Matches,
            observation => observation.Id == chart.Reading.ToString());
        using (var marked = await VitalTests.MarkAsync(apps, chart.Session.Client, chart.Patient, chart.VitalsAt, "Synthetic: taken from the wrong patient",
            VitalSign.HeartRate, VitalSign.Systolic))
        {
            Assert.Equal(["8867-4 72 entered-in-error"], (await VitalTests.StatesAsync(apps, chart.Patient)).Where(s => s.StartsWith("8867-4", StringComparison.Ordinal)));
            Assert.Equal(["8480-6 128 entered-in-error none R1", "8462-4 82 entered-in-error none R1"], await VitalTests.ReadingsAsync(apps, chart.Patient));
        }

        foreach (var category in new[] { "vital-signs", "http://terminology.hl7.org/CodeSystem/observation-category|vital-signs" })
        {
            var (_, signs, notes) = Searched<R4.Observation>(await GetAsync(chart.Session.Client, $"/fhir/Observation?patient={chart.Patient}&category={Uri.EscapeDataString(category)}"));
            Assert.Equal(["29463-7", "39156-5", "59408-5", "8302-2", "8310-5", "9279-1"], signs.Select(o => o.Code.Coding.Single().Code).Order(StringComparer.Ordinal));
            Assert.Empty(notes);
        }

        var (_, results, labNotes) = Searched<R4.Observation>(await GetAsync(chart.Session.Client, $"/fhir/Observation?patient={chart.Patient}&category=laboratory"));
        Assert.Equal(["3016-3", "4548-4"], results.Select(o => o.Code.Coding.Single().Code).Order(StringComparer.Ordinal));
        Assert.Empty(labNotes);
        foreach (var id in new[] { chart.Vitals[VitalSign.HeartRate], chart.Reading, chart.Vitals[VitalSign.Systolic], chart.Vitals[VitalSign.Diastolic] })
        {
            Refused(await GetAsync(chart.Session.Client, $"/fhir/Observation/{id}"), HttpStatusCode.NotFound, "not-found");
        }
    }

    [Fact]
    public async Task A_patient_whose_only_vital_signs_are_blood_pressures_gets_one_observation_for_each_reading()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Fhir Pressure", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Synthetic", "Fhirpressure");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(apps.OnPrem, clinician);
        foreach (var (minutesAgo, systolic, diastolic) in new[] { (90, "131", "85"), (30, "122", "79") })
        {
            using var recorded = await VitalTests.RecordAsync(session.Client, patient, minutesAgo, (VitalSign.Systolic, systolic), (VitalSign.Diastolic, diastolic));
            Assert.Equal(VitalTests.Path(patient), PathOf(recorded));
        }

        var before = await ReadsAsync(apps, clinician.Id, patient);
        var (bundle, found, notes) = Searched<R4.Observation>(await GetAsync(session.Client, $"/fhir/Observation?patient={patient}"));
        Assert.Equal((2, 2), (bundle.Total, found.Count));
        Assert.Empty(notes);
        Assert.All(found, reading =>
        {
            Assert.Equal(("85354-9", "http://hl7.org/fhir/StructureDefinition/bp"), (reading.Code.Coding.Single().Code, reading.Meta!.Profile.Single()));
            Assert.Equal(["8480-6", "8462-4"], reading.Component.Select(component => component.Code.Coding.Single().Code));
        });
        Assert.Equal(["122/79", "131/85"], found.Select(reading => string.Join("/", reading.Component.Select(component => Assert.IsType<R4.Quantity>(component.Value).Value))));
        var readings = await ProxyTests.QueryAsync(apps, "SELECT DISTINCT reading_id::text FROM ehr.observations WHERE patient_id = @patient", patient);
        Assert.Equal(readings.Order(StringComparer.Ordinal), found.Select(reading => reading.Id).Order(StringComparer.Ordinal));
        foreach (var reading in readings)
        {
            Assert.Equal(reading, FhirConformance.Valid<R4.Observation>(Expect(await GetAsync(session.Client, $"/fhir/Observation/{reading}"), HttpStatusCode.OK).Json).Id);
        }

        Assert.Equal((before.Reads + 3, before.Sensitive), await ReadsAsync(apps, clinician.Id, patient));
    }

    [Fact]
    public async Task A_search_past_the_limit_gives_the_latest_600_and_says_how_many_matched()
    {
        var chart = await ChartAsync(apps, "Dr. Fhir Many");
        var patient = await apps.InsertPatientAsync("Synthetic", "Fhirmany");
        await apps.AssignAsync(chart.Clinician, patient);
        await using (var connection = await apps.OpenDatabaseAsync())
        {
            await using var command = new Npgsql.NpgsqlCommand(
                """
                INSERT INTO ehr.observations (id, patient_id, code, display, status, value, unit, reference_low, reference_high, effective_at, issued_at)
                SELECT gen_random_uuid(), @patient, '2345-7', 'Glucose', 'final', 80 + g % 30, 'mg/dL', 70, 99, now() - make_interval(hours => g), now()
                FROM generate_series(1, 601) AS g
                """,
                connection);
            command.Parameters.AddWithValue("patient", patient);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var (bundle, found, notes) = Searched<R4.Observation>(await GetAsync(chart.Session.Client, $"/fhir/Observation?patient={patient}&category=laboratory"));
        Assert.Equal(601, bundle.Total);
        Assert.Equal(600, found.Count);
        Assert.Equal(["Only the first 600 of the 601 matches, the newest first, are included: this server has no paging."], notes);
        Assert.Equal(R4.OperationOutcome.IssueSeverity.Warning,
            Assert.IsType<R4.OperationOutcome>(bundle.Entry.Single(e => e.Search?.Mode == R4.Bundle.SearchEntryMode.Outcome).Resource).Issue.Single().Severity);
        // The oldest, taken 601 hours ago, is the one left out.
        Assert.True(found.Min(o => Assert.IsType<R4.FhirDateTime>(o.Effective).ToDateTimeOffset(TimeSpan.Zero)) > DateTimeOffset.UtcNow.AddHours(-601));
    }
}
