namespace Ehr.Data.Patients;

// The database functions the name search runs on what was typed (PhiReader): it is lower-cased by the same lower() that
// keeps the patients' name keys (PatientConfiguration), under the same collation, so the two can't disagree after an
// upgrade of .NET or PostgreSQL, and the end of the range is worked out from that. Only for queries, where EF Core sends
// them to PostgreSQL (EhrDbContext maps them); called in .NET, they throw.
public static class NameKeys
{
    // PostgreSQL's lower(), under the database's own collation, as the keys' generated columns call it.
    public static string Lower(string value) =>
        throw new InvalidOperationException("NameKeys.Lower runs only inside a query, in the database.");

    // ehr.name_key_after(prefix), from the migration PatientSearchIndexes: the first string after every string that
    // starts with the prefix, in code point order, the "C" collation's. Null for a prefix of nothing but U+10FFFF.
    public static string? After(string prefix) =>
        throw new InvalidOperationException("NameKeys.After runs only inside a query, in the database.");
}
