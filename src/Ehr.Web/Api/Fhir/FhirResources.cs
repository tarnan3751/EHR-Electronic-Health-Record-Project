using System.Globalization;
using System.Text.Json.Serialization;
using Ehr.Data.Patients;
using Ehr.Domain.Scheduling;

namespace Ehr.Web.Api.Fhir;

// What the FHIR API writes (the expansion plan's step 11): the parts of FHIR R4's JSON it uses, as small records that
// System.Text.Json writes, with nothing empty (FHIR's JSON has no nulls and no empty lists, so the mapping leaves them
// null and the writer leaves them out); the mapping from the PHI reader's rows; and the capability statement, written
// from what FhirApi serves. Observations have their own file, FhirObservations.cs. Every code comes from the record
// as step 2a's reference data coded it, with its standard system; an entry with no code is sent as text. Times are
// instants in UTC. The tests check every response against the R4 specification (tests/Ehr.Web.Tests/FhirConformance.cs).
internal static class FhirResources
{
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

    public static FhirPatient Patient(Patient patient) => new(
        patient.Id.ToString(),
        [new Identifier("usual", new CodeableConcept([new Coding(IdentifierTypes, "MR")]), MrnSystem, patient.Mrn)],
        [new HumanName("official", patient.FamilyName, [patient.GivenName])],
        patient.Gender,
        Date(patient.BirthDate));

    // "No known allergies" is an entry like any other, with the app's own marker as its code: sent as text alone.
    public static FhirAllergyIntolerance Allergy(AllergyIntolerance allergy) => new(
        allergy.Id.ToString(),
        new CodeableConcept([new Coding("http://terminology.hl7.org/CodeSystem/allergyintolerance-clinical", allergy.ClinicalStatus)]),
        allergy.Category is { } category ? [category] : null,
        allergy.Criticality,
        Concept(allergy.CodeSystem switch
        {
            ClinicalCodeSystem.RxNorm => RxNorm,
            ClinicalCodeSystem.Unii => Unii,
            _ => null,
        }, allergy.Code, allergy.Display),
        PatientReference(allergy.PatientId),
        Instant(allergy.RecordedAt),
        // A reaction is what the patient has; its severity alone, with no reaction recorded, has nothing to belong to.
        allergy.Reaction is { Length: > 0 } reaction ? [new Reaction([new CodeableConcept(Text: reaction)], allergy.Severity)] : null);

    public static FhirMedicationStatement Medication(MedicationStatement medication) => new(
        medication.Id.ToString(),
        medication.Sensitive ? Restricted : null,
        medication.Status,
        Concept(RxNorm, medication.Code, medication.Display),
        PatientReference(medication.PatientId),
        medication.EffectiveStart is null && medication.EffectiveEnd is null ? null : new Period(Date(medication.EffectiveStart), Date(medication.EffectiveEnd)),
        medication.Dosage is { Length: > 0 } dosage ? [new Dosage(dosage)] : null);

    // The chart's diagnoses are the patient's problem list.
    public static FhirCondition Condition(Condition condition) => new(
        condition.Id.ToString(),
        condition.Sensitive ? Restricted : null,
        new CodeableConcept([new Coding("http://terminology.hl7.org/CodeSystem/condition-clinical", condition.ClinicalStatus)]),
        [new CodeableConcept([new Coding("http://terminology.hl7.org/CodeSystem/condition-category", "problem-list-item")])],
        Concept(Icd10Cm, condition.Code, condition.Display),
        PatientReference(condition.PatientId),
        Date(condition.OnsetDate));

    // The patient and the clinician, each by reference with their name. There is no Practitioner endpoint: the name is
    // what a reader has. FHIR requires each participant's status, which the record doesn't hold: the clinic booked the
    // appointment for both, so both accepted (Tahseen's decision).
    public static FhirAppointment Appointment(AppointmentEntry appointment) => new(
        appointment.Id.ToString(),
        appointment.Status,
        appointment.CancelReason is { Length: > 0 } cancelled ? new CodeableConcept(Text: cancelled) : null,
        [new CodeableConcept(Text: appointment.Reason)],
        Instant(appointment.StartsAt),
        Instant(appointment.StartsAt.AddMinutes(appointment.Minutes)),
        appointment.Minutes,
        [
            new Participant(new ResourceReference($"Patient/{appointment.PatientId}", appointment.PatientName), "accepted"),
            new Participant(new ResourceReference($"Practitioner/{appointment.ClinicianId}", appointment.Clinician), "accepted"),
        ]);

    public static ResourceReference PatientReference(Guid patientId) => new($"Patient/{patientId}");

    // A code with its system when the record has both, and the record's own name as the text; the text alone otherwise.
    // No display: the name is the record's, which may not be the code system's own wording.
    public static CodeableConcept Concept(string? system, string? code, string text) =>
        system is not null && code is { Length: > 0 } ? new CodeableConcept([new Coding(system, code)], text) : new CodeableConcept(Text: text);

    // An instant, in UTC, to the microsecond PostgreSQL keeps, with no trailing zeros.
    public static string Instant(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    public static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    static string? Date(DateOnly? value) => value is { } date ? Date(date) : null;

    // What /fhir/metadata says: every type FhirApi serves, read and searched, with the parameters each takes, and the
    // profiles its vital signs claim. Written from FhirApi.Types, so it can't say more than is served; a test checks
    // that everything it declares works.
    public static FhirCapabilityStatement Capabilities(string baseUrl) => new(
        "SwiftChartReadOnlyFhirApi",
        "SwiftChart read-only FHIR API",
        "active",
        "2026-11-19",
        "instance",
        new Implementation("SwiftChart EHR: a read-only FHIR R4 API for clinicians, with their sign-in to the clinical app.", baseUrl),
        "4.0.1",
        ["json"],
        [
            new Rest(
                "server",
                "Read-only: reads and searches by patient, nothing else. Every response is application/fhir+json, never cached; a read or search "
                + "whose Accept header doesn't take application/fhir+json is refused with 406 (the most specific matching range decides, as RFC "
                + "9110 has it; application/json is taken as asking for it), and any other method with 405. A search needs its patient (or, for Patient, the MRN), returns at most "
                + $"{FhirApi.SearchLimit} entries, with total giving how many matched, and has no paging. {FhirApi.SearchOrder} A parameter not "
                + "listed here is refused with 400, here as everywhere; _format is taken for JSON. Sensitive diagnoses and medications are left "
                + "out unless show-sensitive=true is given, which is recorded, and are then marked restricted. A blood pressure is one "
                + "Observation for each reading, LOINC 85354-9 with its systolic and diastolic as components, whose id is the reading's: its "
                + "two values are not Observations of their own.",
                new RestSecurity(false,
                    "Sign in to the clinical app as a clinician (its cookie, with the second factor). A patient's records are served only while the "
                    + "patient is on your panel or under emergency access you started in the clinical app; each read is recorded as the pages' are."),
                [.. FhirApi.Types.Select(type => new RestResource(
                    type.Key,
                    type.Key == "Observation" ? [.. FhirObservations.Profiles] : null,
                    [new Interaction("read"), new Interaction("search-type")],
                    [.. type.Value.Search.Select(name => SearchParameters[(type.Key, name)])]))]),
        ]);

    // What each search parameter is, by type and name: FHIR's own definition, or this API's documentation for its own.
    static readonly Dictionary<(string Type, string Name), SearchParam> SearchParameters = new()
    {
        [("Patient", "identifier")] = new("identifier", "http://hl7.org/fhir/SearchParameter/Patient-identifier", "token",
            $"The MRN, alone or as {MrnSystem}|MRN. Finds the patient only while you may read their record; any other MRN, a patient's or "
            + "nobody's, finds nothing, the same empty Bundle, and nothing is recorded."),
        [("AllergyIntolerance", "patient")] = PatientParameter("clinical-patient"),
        [("MedicationStatement", "patient")] = PatientParameter("clinical-patient"),
        [("Condition", "patient")] = PatientParameter("clinical-patient"),
        [("Observation", "patient")] = PatientParameter("clinical-patient"),
        [("Appointment", "patient")] = PatientParameter("Appointment-patient"),
        [("Observation", "category")] = new("category", "http://hl7.org/fhir/SearchParameter/Observation-category", "token",
            "laboratory or vital-signs, alone or with the system http://terminology.hl7.org/CodeSystem/observation-category."),
        [("MedicationStatement", FhirApi.ShowSensitive)] = ShowSensitive(),
        [("Condition", FhirApi.ShowSensitive)] = ShowSensitive(),
    };

    static SearchParam PatientParameter(string definition) => new("patient", $"http://hl7.org/fhir/SearchParameter/{definition}", "reference",
        "Required: the patient's ID, or Patient/ID.");

    static SearchParam ShowSensitive() => new(FhirApi.ShowSensitive, null, "token",
        "true to include sensitive entries, marked restricted (meta.security R); also taken by a read. Asking is recorded as a view of "
        + "the patient's sensitive items. Not a filter: without it, sensitive entries are left out.");
}

// The R4 shapes this API writes. A property left null isn't written.

internal abstract record FhirResource(
    [property: JsonPropertyOrder(-2)] string ResourceType,
    [property: JsonPropertyOrder(-1)] string? Id);

internal sealed record Meta(IReadOnlyList<string>? Profile = null, IReadOnlyList<Coding>? Security = null);

internal sealed record Coding(string System, string Code);

internal sealed record CodeableConcept(IReadOnlyList<Coding>? Coding = null, string? Text = null);

internal sealed record ResourceReference(string Reference, string? Display = null);

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

internal sealed record BundleEntry(string FullUrl, object Resource, BundleSearch Search);

internal sealed record FhirBundle(string Type, int Total, IReadOnlyList<BundleLink> Link, IReadOnlyList<BundleEntry>? Entry) : FhirResource("Bundle", null);

internal sealed record Implementation(string Description, string Url);

internal sealed record RestSecurity(bool Cors, string Description);

internal sealed record Interaction(string Code);

internal sealed record SearchParam(string Name, string? Definition, string Type, string Documentation);

internal sealed record RestResource(string Type, IReadOnlyList<string>? SupportedProfile, IReadOnlyList<Interaction> Interaction, IReadOnlyList<SearchParam> SearchParam);

internal sealed record Rest(string Mode, string Documentation, RestSecurity Security, IReadOnlyList<RestResource> Resource);

internal sealed record FhirCapabilityStatement(
    string Name, string Title, string Status, string Date, string Kind, Implementation Implementation, string FhirVersion, IReadOnlyList<string> Format,
    IReadOnlyList<Rest> Rest)
    : FhirResource("CapabilityStatement", null);
