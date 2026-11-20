using Ehr.Data.Patients;
using Ehr.Domain.Phi;
using Ehr.Domain.Results;

namespace Ehr.Web.Api.Fhir;

// Test results and vital signs as the FHIR API sends them (the expansion plan's step 11).
//
// Each carries its category (laboratory or vital-signs), its LOINC code and, when its unit is its code's UCUM unit
// (ehr.observation_codes), that unit with UCUM's system and code; otherwise the unit as text. Vital signs claim FHIR R4's
// vital-sign profiles in meta.profile (Tahseen's decision, 2026-11-19), and the tests validate each against its
// profile. Oxygen saturation claims the base vital-signs profile: R4's own oxygensat profile asks for LOINC 2708-6
// (oxygen saturation in arterial blood), and the record holds 59408-5 (the same, by pulse oximetry), a code this API
// sends as it is rather than add another beside it. A vital sign that couldn't conform (no value, or a unit that isn't its
// code's UCUM unit) claims no profile.
//
// A blood pressure is one Observation for each reading (Tahseen's decision; step 9c gave readings their IDs): LOINC
// 85354-9, claiming R4's bp profile, with its systolic (8480-6) and diastolic (8462-4) as components, each with its value,
// unit and reference range, as the other vital signs have theirs. Its ID is the reading's, which the database gives the
// two values entered together; neither value is an Observation of its own, and a read by either value's ID finds
// nothing. The PHI reader brings the two together by that ID, never by time (PhiReader.ObservationsAsync), and the
// reading is a BloodPressure, as the pages have it.
internal static class FhirObservations
{
    // LOINC's blood pressure panel, the code R4's bp profile requires.
    public const string BloodPressurePanel = "85354-9";

    const string Profile = "http://hl7.org/fhir/StructureDefinition/";

    // Each vital sign's R4 profile, by LOINC code: a blood pressure's is the panel's.
    static readonly IReadOnlyDictionary<string, string> VitalSignProfiles = new Dictionary<string, string>
    {
        [BloodPressurePanel] = Profile + "bp",
        [VitalSign.HeartRate] = Profile + "heartrate",
        [VitalSign.Temperature] = Profile + "bodytemp",
        [VitalSign.RespiratoryRate] = Profile + "resprate",
        [VitalSign.OxygenSaturation] = Profile + "vitalsigns",
        [VitalSign.Weight] = Profile + "bodyweight",
        [VitalSign.Height] = Profile + "bodyheight",
        [VitalSign.BodyMassIndex] = Profile + "bmi",
    };

    static readonly IReadOnlyList<CodeableConcept> VitalSignsCategory =
        [new CodeableConcept([new Coding("http://terminology.hl7.org/CodeSystem/observation-category", ObservationCategory.VitalSigns)])];

    // The profiles vital signs claim, for the capability statement's supportedProfile.
    public static IEnumerable<string> Profiles => VitalSignProfiles.Values.Distinct();

    // Each entry as an Observation, with its ID: a blood pressure's is its reading's. A blood pressure value without the
    // other value of its reading is left out, never sent alone under its own ID: the database keeps readings whole, so
    // this is a guard against what can't be stored, not a case that happens.
    public static IReadOnlyList<(Guid Id, FhirObservation Resource)> Map(IEnumerable<ObservationEntry> entries) =>
        [.. entries.Where(entry => entry.Diastolic is not null || !VitalSign.BloodPressure.Contains(entry.Observation.Code))
            .Select(entry => entry.Diastolic is { } diastolic
                ? (entry.Observation.ReadingId!.Value, Reading(entry, diastolic))
                : (entry.Observation.Id, Map(entry)))];

    static FhirObservation Map(ObservationEntry entry)
    {
        var observation = entry.Observation;
        var profile = observation.Category == ObservationCategory.VitalSigns && entry.UcumUnit && observation.Value is not null
            && observation.Code is not null && VitalSignProfiles.TryGetValue(observation.Code, out var claimed)
                ? claimed
                : null;
        return new FhirObservation(
            observation.Id.ToString(),
            profile is null ? null : new Meta(Profile: [profile]),
            observation.Status,
            [new CodeableConcept([new Coding("http://terminology.hl7.org/CodeSystem/observation-category", observation.Category)])],
            FhirResources.Concept(FhirResources.Loinc, observation.Code, observation.Display),
            FhirResources.PatientReference(observation.PatientId),
            FhirResources.Instant(observation.EffectiveAt),
            observation.IssuedAt is { } issued ? FhirResources.Instant(issued) : null,
            Measured(entry, observation.Value),
            observation.Note is { Length: > 0 } note ? [new Annotation(note)] : null,
            Range(entry),
            Component: null);
    }

    // One blood pressure reading. Its two values were entered together and stand together: the time taken is theirs,
    // the status is theirs, and issued is the later of their two, when the reading was whole.
    static FhirObservation Reading(ObservationEntry systolic, ObservationEntry diastolic)
    {
        var reading = new BloodPressure(systolic.Observation, diastolic.Observation);
        var issued = new[] { reading.Systolic.IssuedAt, reading.Diastolic.IssuedAt }.Max();
        return new FhirObservation(
            reading.Systolic.ReadingId!.Value.ToString(),
            systolic.UcumUnit && diastolic.UcumUnit ? new Meta(Profile: [VitalSignProfiles[BloodPressurePanel]]) : null,
            reading.Systolic.Status,
            VitalSignsCategory,
            FhirResources.Concept(FhirResources.Loinc, BloodPressurePanel, "Blood pressure"),
            FhirResources.PatientReference(reading.Systolic.PatientId),
            FhirResources.Instant(reading.TakenAt),
            issued is { } at ? FhirResources.Instant(at) : null,
            ValueQuantity: null,
            Note: null,
            ReferenceRange: null,
            Component: [Component(systolic), Component(diastolic)]);
    }

    static ObservationComponent Component(ObservationEntry entry) => new(
        FhirResources.Concept(FhirResources.Loinc, entry.Observation.Code, entry.Observation.Display), Measured(entry, entry.Observation.Value), Range(entry));

    // A value in the entry's unit. Without the zeros the column's four places add (70.5000): in FHIR a decimal's places
    // say how precise it is.
    static Quantity? Measured(ObservationEntry entry, decimal? value) => value is { } measured
        ? new Quantity(measured / 1.0000000000000000000000000000m, entry.Observation.Unit, entry.UcumUnit ? FhirResources.Ucum : null,
            entry.UcumUnit ? entry.Observation.Unit : null)
        : null;

    static IReadOnlyList<ReferenceRange>? Range(ObservationEntry entry) =>
        entry.Observation.ReferenceLow is null && entry.Observation.ReferenceHigh is null
            ? null
            : [new ReferenceRange(Measured(entry, entry.Observation.ReferenceLow), Measured(entry, entry.Observation.ReferenceHigh))];
}

internal sealed record Quantity(decimal Value, string? Unit, string? System, string? Code);

internal sealed record Annotation(string Text);

internal sealed record ReferenceRange(Quantity? Low, Quantity? High);

internal sealed record ObservationComponent(CodeableConcept Code, Quantity? ValueQuantity, IReadOnlyList<ReferenceRange>? ReferenceRange);

internal sealed record FhirObservation(
    string Id, Meta? Meta, string Status, IReadOnlyList<CodeableConcept> Category, CodeableConcept Code, ResourceReference Subject,
    string EffectiveDateTime, string? Issued, Quantity? ValueQuantity, IReadOnlyList<Annotation>? Note, IReadOnlyList<ReferenceRange>? ReferenceRange,
    IReadOnlyList<ObservationComponent>? Component)
    : FhirResource("Observation", Id);
