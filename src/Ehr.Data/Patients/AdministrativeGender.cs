namespace Ehr.Data.Patients;

// FHIR's administrative gender codes, the values Patient.Gender may hold. The check constraint in
// PatientConfiguration refuses anything else.
public static class AdministrativeGender
{
    public const string Female = "female";

    public const string Male = "male";

    public const string Other = "other";

    public const string Unknown = "unknown";

    public static readonly string[] All = [Female, Male, Other, Unknown];
}
