using Ehr.Data.Patients;
using Ehr.Domain.Phi;

namespace Ehr.Web.Api.Fhir;

// Test results and vital signs as the FHIR API sends them (the expansion plan's step 11), in one place: how a blood
// pressure reading is sent is to be added here once it is decided.
//
// Each carries its category (laboratory or vital-signs), its LOINC code and, when its unit is its code's UCUM unit
// (ehr.observation_codes), that unit with UCUM's system and code; otherwise the unit as text. Vital signs claim FHIR R4's
// vital-sign profiles in meta.profile (Tahseen's decision, 2026-11-19), and the tests validate each against its
// profile. Oxygen saturation claims the base vital-signs profile: R4's own oxygensat profile asks for LOINC 2708-6
// (oxygen saturation in arterial blood), and the record holds 59408-5 (the same, by pulse oximetry), a code this API
// sends as it is rather than add another beside it. A vital sign that couldn't conform (no value, or a unit that isn't its
// code's UCUM unit) claims no profile.
//
// Blood pressure is not served yet. The record keeps a reading's systolic and diastolic as two observations, which
// can't yet be paired reliably (docs/expansion/PLAN.md's decision log, step 11, "stopped, for Tahseen"), and R4's
// profile wants one observation with both as components. So LeftOut keeps both codes out of every read and search (a
// read by ID of one is not found), and a search that covers vital signs says so. When the pairing is decided, Map is
// where the panel is built, and the two codes come out of LeftOut.
internal static class FhirObservations
{
    // The codes the PHI reader leaves out of the API's reads of observations.
    public static readonly IReadOnlyCollection<string> LeftOut = [VitalSign.Systolic, VitalSign.Diastolic];

    // What a search that covers vital signs says beside them, as information: fixed text, about no patient.
    public const string BloodPressureNotServed = "Blood pressure readings are not served by this API yet.";

    const string Profile = "http://hl7.org/fhir/StructureDefinition/";

    // Each vital sign's R4 profile, by LOINC code.
    static readonly IReadOnlyDictionary<string, string> VitalSignProfiles = new Dictionary<string, string>
    {
        [VitalSign.HeartRate] = Profile + "heartrate",
        [VitalSign.Temperature] = Profile + "bodytemp",
        [VitalSign.RespiratoryRate] = Profile + "resprate",
        [VitalSign.OxygenSaturation] = Profile + "vitalsigns",
        [VitalSign.Weight] = Profile + "bodyweight",
        [VitalSign.Height] = Profile + "bodyheight",
        [VitalSign.BodyMassIndex] = Profile + "bmi",
    };

    // The profiles vital signs claim, for the capability statement's supportedProfile.
    public static IEnumerable<string> Profiles => VitalSignProfiles.Values.Distinct();

    // Whether a search of this category (null for any) covers vital signs.
    public static bool CoversVitalSigns(string? category) => category is null or ObservationCategory.VitalSigns;

    public static IReadOnlyList<FhirObservation> Map(IEnumerable<ObservationEntry> entries) => [.. entries.Select(Map)];

    static FhirObservation Map(ObservationEntry entry)
    {
        var observation = entry.Observation;
        // Without the zeros the column's four places add (70.5000): in FHIR a decimal's places say how precise it is.
        Quantity? Measured(decimal? value) => value is { } measured
            ? new Quantity(measured / 1.0000000000000000000000000000m, observation.Unit, entry.UcumUnit ? FhirResources.Ucum : null,
                entry.UcumUnit ? observation.Unit : null)
            : null;
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
            Measured(observation.Value),
            observation.Note is { Length: > 0 } note ? [new Annotation(note)] : null,
            observation.ReferenceLow is null && observation.ReferenceHigh is null
                ? null
                : [new ReferenceRange(Measured(observation.ReferenceLow), Measured(observation.ReferenceHigh))]);
    }
}

internal sealed record Quantity(decimal Value, string? Unit, string? System, string? Code);

internal sealed record Annotation(string Text);

internal sealed record ReferenceRange(Quantity? Low, Quantity? High);

internal sealed record FhirObservation(
    string Id, Meta? Meta, string Status, IReadOnlyList<CodeableConcept> Category, CodeableConcept Code, ResourceReference Subject,
    string EffectiveDateTime, string? Issued, Quantity? ValueQuantity, IReadOnlyList<Annotation>? Note, IReadOnlyList<ReferenceRange>? ReferenceRange)
    : FhirResource("Observation", Id);
