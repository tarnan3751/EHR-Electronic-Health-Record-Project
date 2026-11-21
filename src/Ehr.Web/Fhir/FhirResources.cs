using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ehr.Data.Patients;
using Ehr.Domain.Scheduling;

namespace Ehr.Web.Fhir;

// The FHIR R4 the app writes (the expansion plan's step 11): the parts of FHIR R4's JSON it uses, as small records that
// System.Text.Json writes, with nothing empty (FHIR's JSON has no nulls and no empty lists, so the mapping leaves them
// null and the writer leaves them out), and the mapping from the PHI reader's rows. Observations have their own file,
// FhirObservations.cs. It is kept apart from the clinician API that serves it (Api/Fhir, which writes its capability
// statement itself, FhirCapabilities.cs), so that another door can send the same resources from the same mapping. Every
// code comes from the record as step 2a's reference data coded it, with its standard system; an entry with no code is
// sent as text. Times are instants in UTC. The tests check every response against the R4 specification
// (tests/Ehr.Web.Tests/FhirConformance.cs).
internal static class FhirResources
{
    public const string ContentType = "application/fhir+json; charset=utf-8";

    // How every resource is written: FHIR's JSON names, and nothing null.
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public const string RxNorm = "http://www.nlm.nih.gov/research/umls/rxnorm";

    public const string Unii = "http://fdasis.nlm.nih.gov";

    public const string Icd10Cm = "http://hl7.org/fhir/sid/icd-10-cm";

    public const string Loinc = "http://loinc.org";

    public const string Ucum = "http://unitsofmeasure.org";

    // The clinic's own namespace for its medical record numbers (Tahseen's decision, 2026-11-19). Recorded in
    // docs/expansion/reference-data.md.
    public const string MrnSystem = "http://swiftchart.example/fhir/sid/mrn";

    const string IdentifierTypes = "http://terminology.hl7.org/CodeSystem/v2-0203";

    const string Confidentiality = "http://terminology.hl7.org/CodeSystem/v3-Confidentiality";

    // A sensitive diagnosis or medication, shown because it was asked for: restricted, in FHIR's confidentiality codes.
    static readonly Meta Restricted = new(Security: [new Coding(Confidentiality, "R")]);

    // Each mapping writes its entry's id, and its references, as the ids say: the record's own for the clinician API, or a
    // download's own (FhirIds).
    public static FhirPatient Patient(Patient patient, FhirIds ids) => new(
        ids.Id(patient.Id),
        [new Identifier("usual", new CodeableConcept([new Coding(IdentifierTypes, "MR")]), MrnSystem, patient.Mrn)],
        [new HumanName("official", patient.FamilyName, [patient.GivenName])],
        patient.Gender,
        Date(patient.BirthDate));

    // "No known allergies" is an entry like any other, with the app's own marker as its code: sent as text alone.
    public static FhirAllergyIntolerance Allergy(AllergyIntolerance allergy, FhirIds ids) => new(
        ids.Id(allergy.Id),
        new CodeableConcept([new Coding("http://terminology.hl7.org/CodeSystem/allergyintolerance-clinical", allergy.ClinicalStatus)]),
        allergy.Category is { } category ? [category] : null,
        allergy.Criticality,
        Concept(allergy.CodeSystem switch
        {
            ClinicalCodeSystem.RxNorm => RxNorm,
            ClinicalCodeSystem.Unii => Unii,
            _ => null,
        }, allergy.Code, allergy.Display),
        ids.Patient(allergy.PatientId),
        Instant(allergy.RecordedAt),
        // A reaction is what the patient has; its severity alone, with no reaction recorded, has nothing to belong to.
        allergy.Reaction is { Length: > 0 } reaction ? [new Reaction([new CodeableConcept(Text: reaction)], allergy.Severity)] : null);

    public static FhirMedicationStatement Medication(MedicationStatement medication, FhirIds ids) => new(
        ids.Id(medication.Id),
        medication.Sensitive ? Restricted : null,
        medication.Status,
        Concept(RxNorm, medication.Code, medication.Display),
        ids.Patient(medication.PatientId),
        medication.EffectiveStart is null && medication.EffectiveEnd is null ? null : new Period(Date(medication.EffectiveStart), Date(medication.EffectiveEnd)),
        medication.Dosage is { Length: > 0 } dosage ? [new Dosage(dosage)] : null);

    // The chart's diagnoses are the patient's problem list.
    public static FhirCondition Condition(Condition condition, FhirIds ids) => new(
        ids.Id(condition.Id),
        condition.Sensitive ? Restricted : null,
        new CodeableConcept([new Coding("http://terminology.hl7.org/CodeSystem/condition-clinical", condition.ClinicalStatus)]),
        [new CodeableConcept([new Coding("http://terminology.hl7.org/CodeSystem/condition-category", "problem-list-item")])],
        Concept(Icd10Cm, condition.Code, condition.Display),
        ids.Patient(condition.PatientId),
        Date(condition.OnsetDate));

    // The patient and the clinician, each with their name, and by reference as the ids have it (a download's names the
    // clinician only). There is no Practitioner endpoint: the name is what a reader has. FHIR requires each participant's
    // status, which the record doesn't hold: the clinic booked the appointment for both, so both accepted (Tahseen's
    // decision).
    public static FhirAppointment Appointment(AppointmentEntry appointment, FhirIds ids) => new(
        ids.Id(appointment.Id),
        appointment.Status,
        appointment.CancelReason is { Length: > 0 } cancelled ? new CodeableConcept(Text: cancelled) : null,
        [new CodeableConcept(Text: appointment.Reason)],
        Instant(appointment.StartsAt),
        Instant(appointment.StartsAt.AddMinutes(appointment.Minutes)),
        appointment.Minutes,
        [
            new Participant(ids.Patient(appointment.PatientId, appointment.PatientName), "accepted"),
            new Participant(ids.Practitioner(appointment.ClinicianId, appointment.Clinician), "accepted"),
        ]);

    // A code with its system when the record has both, and the record's own name as the text; the text alone otherwise.
    // No display: the name is the record's, which may not be the code system's own wording.
    public static CodeableConcept Concept(string? system, string? code, string text) =>
        system is not null && code is { Length: > 0 } ? new CodeableConcept([new Coding(system, code)], text) : new CodeableConcept(Text: text);

    // An instant, in UTC, to the microsecond PostgreSQL keeps, with no trailing zeros.
    public static string Instant(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    public static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    static string? Date(DateOnly? value) => value is { } date ? Date(date) : null;
}

// The R4 shapes the app writes. A property left null isn't written.

internal abstract record FhirResource(
    [property: JsonPropertyOrder(-2)] string ResourceType,
    [property: JsonPropertyOrder(-1)] string? Id);

internal sealed record Meta(IReadOnlyList<string>? Profile = null, IReadOnlyList<Coding>? Security = null);

internal sealed record Coding(string System, string Code);

internal sealed record CodeableConcept(IReadOnlyList<Coding>? Coding = null, string? Text = null);

// A reference by address, with a name, or by name alone.
internal sealed record ResourceReference(string? Reference, string? Display = null);

internal sealed record Identifier(string Use, CodeableConcept Type, string System, string Value);

internal sealed record HumanName(string Use, string Family, IReadOnlyList<string> Given);

internal sealed record Period(string? Start, string? End);

internal sealed record Dosage(string Text);

internal sealed record Reaction(IReadOnlyList<CodeableConcept> Manifestation, string? Severity);

internal sealed record Participant(ResourceReference Actor, string Status);

internal sealed record FhirPatient(string Id, IReadOnlyList<Identifier> Identifier, IReadOnlyList<HumanName> Name, string Gender, string BirthDate)
    : FhirResource("Patient", Id);

internal sealed record FhirAllergyIntolerance(
    string Id, CodeableConcept ClinicalStatus, IReadOnlyList<string>? Category, string? Criticality, CodeableConcept Code, ResourceReference Patient,
    string RecordedDate, IReadOnlyList<Reaction>? Reaction)
    : FhirResource("AllergyIntolerance", Id);

internal sealed record FhirMedicationStatement(
    string Id, Meta? Meta, string Status, CodeableConcept MedicationCodeableConcept, ResourceReference Subject, Period? EffectivePeriod,
    IReadOnlyList<Dosage>? Dosage)
    : FhirResource("MedicationStatement", Id);

internal sealed record FhirCondition(
    string Id, Meta? Meta, CodeableConcept ClinicalStatus, IReadOnlyList<CodeableConcept> Category, CodeableConcept Code, ResourceReference Subject,
    string? OnsetDateTime)
    : FhirResource("Condition", Id);

internal sealed record FhirAppointment(
    string Id, string Status, CodeableConcept? CancelationReason, IReadOnlyList<CodeableConcept> ReasonCode, string Start, string End, int MinutesDuration,
    IReadOnlyList<Participant> Participant)
    : FhirResource("Appointment", Id);

internal sealed record Issue(string Severity, string Code, string Diagnostics);

internal sealed record FhirOperationOutcome(IReadOnlyList<Issue> Issue) : FhirResource("OperationOutcome", null);

internal sealed record BundleLink(string Relation, string Url);

internal sealed record BundleSearch(string Mode);

// A search's entries say whether each matched or is what the search had to say; other bundles' entries say neither.
internal sealed record BundleEntry(string FullUrl, object Resource, BundleSearch? Search = null);

// A search's bundle has its total and its self link; a collection, when it was put together.
internal sealed record FhirBundle(string Type, string? Timestamp, int? Total, IReadOnlyList<BundleLink>? Link, IReadOnlyList<BundleEntry>? Entry)
    : FhirResource("Bundle", null);
