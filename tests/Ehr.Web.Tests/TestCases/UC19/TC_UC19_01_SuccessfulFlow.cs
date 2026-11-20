using System.Net;
using Ehr.Data.Patients;
using Xunit;
using static Ehr.Web.Tests.FhirRequests;
using R4 = Hl7.Fhir.Model;

namespace Ehr.Web.Tests.TestCases.UC19;

// Test Case ID: TC-UC19-01
// Purpose / scenario: A clinician reads a patient on their panel through the FHIR API: the capability statement, the
//   patient by ID and by MRN, and each kind of entry the API serves, searched by patient and read by ID. Every answer is
//   valid FHIR R4, coded as the record is, and each read of the patient is recorded once, as a page's is.
// Type: Functional (successful flow)
// Requirements: proposed FR-53, with FR-32
// Use case: UC 19, Read a patient's record through the FHIR API
// Preconditions: The clinician is signed in on the on-site app, with the patient on their panel. The patient's record
//   holds two allergies, two medications (one sensitive), two diagnoses (one sensitive), two results, a set of vital
//   signs and two appointments, one cancelled (FhirRequests.ChartAsync).
// Inputs and system conditions: GET /fhir/metadata; GET /fhir/Patient/{id}; GET /fhir/Patient?identifier={MRN}, and
//   with the clinic's MRN system; GET /fhir/{type}?patient={id} and GET /fhir/{type}/{id} for AllergyIntolerance,
//   MedicationStatement, Condition, Observation and Appointment; on the on-site app.
// Test data: Patient "Synthetic Fhirchart"; penicillin G (RxNorm 7980) and peanut (UNII QE1QX6B99R); lisinopril (RxNorm
//   314076); hypertension (ICD-10-CM I10); hemoglobin A1c 6.9 % (LOINC 4548-4) and thyrotropin 2.1 "mIU/L" (3016-3);
//   vital signs 128/82 mmHg, 72/min, 36.8 Cel, 16/min, 97 %, 70.5 kg, 172 cm; appointments "Synthetic: blood pressure
//   check" and "Synthetic: medication review", cancelled with "Synthetic: the patient asked to move it".
// Steps:
//   1. Read the capability statement.
//   2. Read the patient by ID, then search for them by MRN, bare and with the system.
//   3. Search each kind of entry by the patient, then read one or two of each by ID.
//   4. Count the reads recorded about the patient.
// Expected result: Every answer is application/fhir+json, Cache-Control no-store, and parses strictly and validates
//   against R4 and the profiles it claims, with no error or warning. Step 1 lists the six types, read and search-type
//   each. Step 2: the MRN with the system http://swiftchart.example/fhir/sid/mrn and type MR, the name, and the birth
//   date; each search finds the one patient, with its address under the configured site address. Step 3: each code
//   with its system (RxNorm, UNII, ICD-10-CM, LOINC), the hemoglobin A1c in UCUM and the thyrotropin's unit as text
//   only; each vital sign in the vital-signs category, in UCUM, claiming its R4 profile (oxygen saturation the base
//   vital-signs profile); no blood pressure reading, and a note that they aren't served yet; times in UTC; the
//   appointments with their status, start, end, length, reason and participants, and the cancellation's reason; the
//   sensitive medication and diagnosis left out. Step 4: one recorded read for each request of steps 2 and 3, fifteen,
//   and none for step 1; no read of sensitive items.
// Creator: Tahseen Arnan
public class TC_UC19_01_SuccessfulFlow(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_clinician_reads_each_kind_of_entry_as_valid_R4_and_each_read_is_recorded_once()
    {
        var chart = await ChartAsync(apps, "Dr. Fhir Reader");
        var client = chart.Session.Client;
        var before = await ReadsAsync(apps, chart.Clinician.Id, chart.Patient);

        // 1. What the API serves.
        var capabilities = FhirConformance.Valid<R4.CapabilityStatement>(Expect(await GetAsync(client, "/fhir/metadata"), HttpStatusCode.OK).Json);
        Assert.Equal(R4.FHIRVersion.N4_0_1, capabilities.FhirVersion);
        Assert.Equal(["Patient", "AllergyIntolerance", "MedicationStatement", "Condition", "Observation", "Appointment"],
            capabilities.Rest.Single().Resource.Select(resource => resource.Type));
        Assert.Equal(before, await ReadsAsync(apps, chart.Clinician.Id, chart.Patient));

        // 2. The patient, by ID and by MRN.
        var patient = FhirConformance.Valid<R4.Patient>(Expect(await GetAsync(client, $"/fhir/Patient/{chart.Patient}"), HttpStatusCode.OK).Json);
        var mrn = Assert.Single(patient.Identifier);
        Assert.Equal(("http://swiftchart.example/fhir/sid/mrn", chart.Mrn, "MR"), (mrn.System, mrn.Value, mrn.Type!.Coding.Single().Code));
        Assert.Equal(("Fhirchart", "Synthetic"), (patient.Name.Single().Family, patient.Name.Single().Given.Single()));
        Assert.Equal("1980-01-01", patient.BirthDate);
        foreach (var identifier in new[] { chart.Mrn, $"http://swiftchart.example/fhir/sid/mrn|{chart.Mrn}" })
        {
            var (bundle, found, _) = Searched<R4.Patient>(await GetAsync(client, $"/fhir/Patient?identifier={Uri.EscapeDataString(identifier)}"));
            Assert.Equal(1, bundle.Total);
            Assert.Equal(chart.Patient.ToString(), Assert.Single(found).Id);
            Assert.StartsWith("https://localhost/fhir/Patient?identifier=", bundle.Link.Single(link => link.Relation == "self").Url);
        }

        // 3. Allergies: RxNorm and UNII, with the reaction.
        var (_, allergies, _) = Searched<R4.AllergyIntolerance>(await GetAsync(client, $"/fhir/AllergyIntolerance?patient={chart.Patient}"));
        Assert.Equal(["7980 http://www.nlm.nih.gov/research/umls/rxnorm", "QE1QX6B99R http://fdasis.nlm.nih.gov"],
            allergies.Select(a => $"{a.Code!.Coding.Single().Code} {a.Code.Coding.Single().System}").Order(StringComparer.Ordinal));
        var peanut = FhirConformance.Valid<R4.AllergyIntolerance>(Expect(await GetAsync(client, $"/fhir/AllergyIntolerance/{chart.Peanut}"), HttpStatusCode.OK).Json);
        Assert.Equal(("Peanut", "Anaphylaxis", R4.AllergyIntolerance.AllergyIntoleranceSeverity.Severe),
            (peanut.Code!.Text, peanut.Reaction.Single().Manifestation.Single().Text, peanut.Reaction.Single().Severity));
        Assert.Equal($"Patient/{chart.Patient}", peanut.Patient.Reference);
        Expect(await GetAsync(client, $"/fhir/AllergyIntolerance/{chart.Penicillin}"), HttpStatusCode.OK);

        // Medications and diagnoses: the sensitive ones left out (TC-UC19-02 asks for them).
        var (_, medications, _) = Searched<R4.MedicationStatement>(await GetAsync(client, $"/fhir/MedicationStatement?patient={chart.Patient}"));
        var lisinopril = Assert.Single(medications);
        Assert.Equal(("314076", "http://www.nlm.nih.gov/research/umls/rxnorm"), (((R4.CodeableConcept)lisinopril.Medication).Coding.Single().Code,
            ((R4.CodeableConcept)lisinopril.Medication).Coding.Single().System));
        Assert.Null(lisinopril.Meta);
        Expect(await GetAsync(client, $"/fhir/MedicationStatement/{chart.Lisinopril}"), HttpStatusCode.OK);
        var (_, conditions, _) = Searched<R4.Condition>(await GetAsync(client, $"/fhir/Condition?patient={chart.Patient}"));
        var hypertension = Assert.Single(conditions);
        Assert.Equal(("I10", "http://hl7.org/fhir/sid/icd-10-cm", "problem-list-item", "2021-05-01"),
            (hypertension.Code!.Coding.Single().Code, hypertension.Code.Coding.Single().System, hypertension.Category.Single().Coding.Single().Code,
                Assert.IsType<R4.FhirDateTime>(hypertension.Onset).Value));
        Expect(await GetAsync(client, $"/fhir/Condition/{chart.Hypertension}"), HttpStatusCode.OK);

        // Results and vital signs.
        var observations = await GetAsync(client, $"/fhir/Observation?patient={chart.Patient}");
        var (_, found2, notes) = Searched<R4.Observation>(observations);
        var byCode = found2.ToDictionary(o => o.Code!.Coding.Single().Code!);
        Assert.Equal(["29463-7", "3016-3", "39156-5", "4548-4", "59408-5", "8302-2", "8310-5", "8867-4", "9279-1"], byCode.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("Blood pressure readings are not served by this API yet.", notes);
        Assert.All(found2, o => Assert.Equal("http://loinc.org", o.Code.Coding.Single().System));
        var a1c = byCode["4548-4"];
        var a1cValue = Assert.IsType<R4.Quantity>(a1c.Value);
        Assert.Equal(("laboratory", 6.9m, "%", "http://unitsofmeasure.org"), (a1c.Category.Single().Coding.Single().Code, a1cValue.Value, a1cValue.Code, a1cValue.System));
        Assert.Equal("Synthetic: we'll talk about this at your next visit.", a1c.Note.Single().Text);
        var thyrotropin = Assert.IsType<R4.Quantity>(byCode["3016-3"].Value);
        Assert.Equal(("mIU/L", null, null), (thyrotropin.Unit, thyrotropin.Code, thyrotropin.System));
        foreach (var (code, profile, unit) in new[]
        {
            (VitalSign.HeartRate, "heartrate", "/min"), (VitalSign.Temperature, "bodytemp", "Cel"), (VitalSign.RespiratoryRate, "resprate", "/min"),
            (VitalSign.OxygenSaturation, "vitalsigns", "%"), (VitalSign.Weight, "bodyweight", "kg"), (VitalSign.Height, "bodyheight", "cm"),
            (VitalSign.BodyMassIndex, "bmi", "kg/m2"),
        })
        {
            var sign = byCode[code];
            Assert.Equal("vital-signs", sign.Category.Single().Coding.Single().Code);
            Assert.Equal($"http://hl7.org/fhir/StructureDefinition/{profile}", sign.Meta!.Profile.Single());
            var quantity = Assert.IsType<R4.Quantity>(sign.Value);
            Assert.Equal((unit, "http://unitsofmeasure.org"), (quantity.Code, quantity.System));
        }

        Assert.Matches("\"effectiveDateTime\":\"[0-9-]{10}T[0-9:.]+Z\"", observations.Json);
        // A decimal as precise as it was recorded, not as the column keeps it.
        Assert.Contains("\"value\":70.5,", observations.Json);
        foreach (var id in new[] { chart.Vitals[VitalSign.HeartRate], chart.HemoglobinA1c })
        {
            Expect(await GetAsync(client, $"/fhir/Observation/{id}"), HttpStatusCode.OK);
        }

        // Appointments: booked, and cancelled with its reason.
        var (_, appointments, _) = Searched<R4.Appointment>(await GetAsync(client, $"/fhir/Appointment?patient={chart.Patient}"));
        Assert.Equal([R4.Appointment.AppointmentStatus.Cancelled, R4.Appointment.AppointmentStatus.Booked], appointments.Select(a => a.Status!.Value));
        var cancelled = appointments[0];
        Assert.Equal("Synthetic: the patient asked to move it", cancelled.CancelationReason!.Text);
        var booked = FhirConformance.Valid<R4.Appointment>(Expect(await GetAsync(client, $"/fhir/Appointment/{chart.Booked}"), HttpStatusCode.OK).Json);
        Assert.Equal(("Synthetic: blood pressure check", 20), (booked.ReasonCode.Single().Text, booked.MinutesDuration));
        Assert.Equal(TimeSpan.FromMinutes(20), booked.End!.Value - booked.Start!.Value);
        Assert.Equal([$"Patient/{chart.Patient} Fhirchart, Synthetic", $"Practitioner/{chart.Clinician.Id} Dr. Fhir Reader"],
            booked.Participant.Select(p => $"{p.Actor!.Reference} {p.Actor.Display}"));
        Assert.All(booked.Participant, p => Assert.Equal(R4.ParticipationStatus.Accepted, p.Status));

        // 4. One read of the patient for each request of steps 2 and 3: three of the patient, three of allergies, two each
        // of medications, diagnoses and appointments, three of results and vital signs. Nothing of sensitive items.
        Assert.Equal((before.Reads + 15, before.Sensitive), await ReadsAsync(apps, chart.Clinician.Id, chart.Patient));
    }
}
