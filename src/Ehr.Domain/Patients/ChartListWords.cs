using Ehr.Data.Patients;

namespace Ehr.Domain.Patients;

// The words the chart's lists use for their stored codes (FHIR's status, severity and criticality values), in one
// place: the entry form offers them, the history page and the chart's "Saved." notice describe a change with them.
// Each list is named by its table.
public static class ChartListWords
{
    public const string Allergies = "allergy_intolerances";

    public const string Medications = "medication_statements";

    public const string Diagnoses = "conditions";

    // What the chart calls the list, as its heading, and one entry of it.
    public static readonly IReadOnlyDictionary<string, (string Heading, string One)> Lists = new Dictionary<string, (string, string)>
    {
        [Allergies] = ("Allergies", "allergy"),
        [Medications] = ("Medications", "medication"),
        [Diagnoses] = ("Diagnoses", "diagnosis"),
    };

    // The statuses a form offers for each list, in its order. A stored status a form doesn't offer (a medication
    // "intended", a diagnosis "relapse") still has its words, below.
    public static IReadOnlyList<(string Code, string Name)> Statuses(string table) => table switch
    {
        Allergies => [(AllergyClinicalStatus.Active, "Active"), (AllergyClinicalStatus.Inactive, "Inactive"), (AllergyClinicalStatus.Resolved, "Resolved")],
        Medications =>
        [
            (MedicationStatementStatus.Active, "Active"), (MedicationStatementStatus.OnHold, "On hold"), (MedicationStatementStatus.Stopped, "Stopped"),
            (MedicationStatementStatus.Completed, "Completed"), (MedicationStatementStatus.EnteredInError, "Entered in error"),
        ],
        _ =>
        [
            (ConditionClinicalStatus.Active, "Active"), (ConditionClinicalStatus.Recurrence, "Recurrence"), (ConditionClinicalStatus.Remission, "Remission"),
            (ConditionClinicalStatus.Inactive, "Inactive"), (ConditionClinicalStatus.Resolved, "Resolved"),
        ],
    };

    public static readonly IReadOnlyList<(string Code, string Name)> Severities =
        [(AllergySeverity.Mild, "Mild"), (AllergySeverity.Moderate, "Moderate"), (AllergySeverity.Severe, "Severe")];

    // The chart tags a high one "High risk"; the form asks for the "Criticality".
    public static readonly IReadOnlyList<(string Code, string Name)> Criticalities =
        [(AllergyCriticality.Low, "Low risk"), (AllergyCriticality.High, "High risk"), (AllergyCriticality.UnableToAssess, "Unable to assess")];

    static readonly Dictionary<string, string> OtherStatuses = new()
    {
        [MedicationStatementStatus.Intended] = "Intended",
        [MedicationStatementStatus.Unknown] = "Unknown",
        [MedicationStatementStatus.NotTaken] = "Not taken",
        [ConditionClinicalStatus.Relapse] = "Relapse",
    };

    // A stored status in words, or the code itself if it is one nothing here knows.
    public static string Status(string table, string code) =>
        Statuses(table).FirstOrDefault(status => status.Code == code).Name ?? OtherStatuses.GetValueOrDefault(code, code);

    public static string Severity(string code) => Severities.FirstOrDefault(severity => severity.Code == code).Name ?? code;

    public static string Criticality(string code) => Criticalities.FirstOrDefault(criticality => criticality.Code == code).Name ?? code;
}
