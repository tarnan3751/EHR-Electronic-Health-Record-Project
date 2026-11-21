using Ehr.Web.Fhir;

namespace Ehr.Web.Api.Fhir;

// The capability statement the FHIR API serves at /fhir/metadata (the expansion plan's step 11), and the R4 shapes it
// is written in. It belongs to the API, which alone serves FHIR's REST interface; the resources themselves are the shared
// mapping's (Fhir/FhirResources.cs, FhirObservations.cs).
internal static class FhirCapabilities
{
    // What /fhir/metadata says: every type FhirApi serves, read and searched, with the parameters each takes, and the
    // profiles its vital signs claim. Written from FhirApi.Types, so it can't say more than is served; a test checks
    // that everything it declares works.
    public static FhirCapabilityStatement Statement(string baseUrl) => new(
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
            $"The MRN, alone or as {FhirResources.MrnSystem}|MRN. Finds the patient only while you may read their record; any other MRN, a patient's or "
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

// The R4 shapes of a capability statement. A property left null isn't written.

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
