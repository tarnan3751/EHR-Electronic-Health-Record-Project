namespace Ehr.Data.Patients;

// For the check constraints that hold a column to a list of FHIR codes.
static class Codes
{
    // The codes as a SQL list: 'active', 'inactive', 'resolved'.
    public static string List(IEnumerable<string> codes) => string.Join(", ", codes.Select(code => $"'{code}'"));
}
