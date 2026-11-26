using System.Net;
using Ehr.Data.Patients;
using Ehr.Domain.Scheduling;
using Ehr.Web.Fhir;
using Xunit;
using static Ehr.Web.Tests.FhirRequests;
using R4 = Hl7.Fhir.Model;

namespace Ehr.Web.Tests;

// Which ids the FHIR mapping writes (FhirIds). The clinician API writes the record's own, as it always has: every
// entry's ID and every reference, for a patient whose record holds one of each thing it serves. A file made for one
// download writes ids of its own, the same one for a record wherever it is written, and refers to the patient by the
// file's own address, never by anything of the clinic's (the portal's download, the expansion plan's step 13b).
public class FhirIdsTests(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task The_api_writes_the_records_own_ids_and_references()
    {
        var chart = await ChartAsync(apps, "Dr. Ownids");
        var client = chart.Session.Client;
        var patient = $"Patient/{chart.Patient}";

        Assert.Equal(chart.Patient.ToString(), FhirConformance.Valid<R4.Patient>(Expect(await GetAsync(client, $"/fhir/Patient/{chart.Patient}"), HttpStatusCode.OK).Json).Id);

        var allergies = Searched<R4.AllergyIntolerance>(await GetAsync(client, $"/fhir/AllergyIntolerance?patient={chart.Patient}")).Matches;
        Assert.Equal(Ids(chart.Penicillin, chart.Peanut), Ids(allergies.Select(a => a.Id)));
        Assert.All(allergies, a => Assert.Equal(patient, a.Patient.Reference));

        var medications = Searched<R4.MedicationStatement>(await GetAsync(client, $"/fhir/MedicationStatement?patient={chart.Patient}&show-sensitive=true")).Matches;
        Assert.Equal(Ids(chart.Lisinopril, chart.Sertraline), Ids(medications.Select(m => m.Id)));
        Assert.All(medications, m => Assert.Equal(patient, m.Subject.Reference));

        var conditions = Searched<R4.Condition>(await GetAsync(client, $"/fhir/Condition?patient={chart.Patient}&show-sensitive=true")).Matches;
        Assert.Equal(Ids(chart.Hypertension, chart.Anxiety), Ids(conditions.Select(c => c.Id)));
        Assert.All(conditions, c => Assert.Equal(patient, c.Subject.Reference));

        // A blood pressure's id is its reading's; every other observation's, its row's.
        var observations = Searched<R4.Observation>(await GetAsync(client, $"/fhir/Observation?patient={chart.Patient}")).Matches;
        Assert.Equal(
            Ids([chart.HemoglobinA1c, chart.Thyrotropin, chart.Reading, .. chart.Vitals.Where(v => v.Key is not (VitalSign.Systolic or VitalSign.Diastolic)).Select(v => v.Value)]),
            Ids(observations.Select(o => o.Id)));
        Assert.All(observations, o => Assert.Equal(patient, o.Subject?.Reference));

        var appointments = Searched<R4.Appointment>(await GetAsync(client, $"/fhir/Appointment?patient={chart.Patient}")).Matches;
        Assert.Equal(Ids(chart.Booked, chart.Cancelled), Ids(appointments.Select(a => a.Id)));
        Assert.All(appointments, a => Assert.Equal([patient, $"Practitioner/{chart.Clinician.Id}"], a.Participant.Select(p => p.Actor?.Reference)));
    }

    [Fact]
    public void A_download_writes_ids_of_its_own_and_the_api_the_records()
    {
        var patient = new Patient { Id = Guid.CreateVersion7(), Mrn = "SYN0000001", GivenName = "Synthetic", FamilyName = "Ids", Gender = "unknown" };
        var condition = new Condition { Id = Guid.CreateVersion7(), PatientId = patient.Id, Display = "Synthetic condition", ClinicalStatus = "active" };
        var appointment = new AppointmentEntry(Guid.CreateVersion7(), patient.Id, "Ids, Synthetic", patient.Mrn, Guid.CreateVersion7(), "Dr. Ids",
            new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero), 30, "Synthetic: check-up", "booked", null);

        var ids = FhirIds.ForDownload();
        var own = FhirResources.Patient(patient, ids);
        var made = Guid.Parse(own.Id!);
        Assert.NotEqual(patient.Id, made);
        Assert.Equal(4, made.Version);
        // One record, one id, wherever the file writes it; the patient by the file's own address.
        Assert.Equal(own.Id, ids.Id(patient.Id));
        Assert.Equal($"urn:uuid:{made}", ids.Address(patient.Id));
        Assert.Equal($"urn:uuid:{made}", FhirResources.Condition(condition, ids).Subject.Reference);
        var booked = FhirResources.Appointment(appointment, ids);
        Assert.Equal([$"urn:uuid:{made}", null], booked.Participant.Select(p => p.Actor.Reference));
        Assert.Equal(["Ids, Synthetic", "Dr. Ids"], booked.Participant.Select(p => p.Actor.Display));
        Assert.DoesNotContain(booked.Id, new[] { appointment.Id.ToString(), condition.Id.ToString(), patient.Id.ToString() });
        // Another download, other ids.
        Assert.NotEqual(own.Id, FhirIds.ForDownload().Id(patient.Id));

        // The API's: the record's own, and no address in a file.
        Assert.Equal(patient.Id.ToString(), FhirResources.Patient(patient, FhirIds.Record).Id);
        Assert.Equal([$"Patient/{patient.Id}", $"Practitioner/{appointment.ClinicianId}"],
            FhirResources.Appointment(appointment, FhirIds.Record).Participant.Select(p => p.Actor.Reference));
        Assert.Throws<InvalidOperationException>(() => FhirIds.Record.Address(patient.Id));
    }

    static List<string> Ids(params IEnumerable<Guid> ids) => [.. ids.Select(id => id.ToString()).Order(StringComparer.Ordinal)];

    static List<string> Ids(IEnumerable<string?> ids) => [.. ids.Select(id => id ?? "").Order(StringComparer.Ordinal)];
}
