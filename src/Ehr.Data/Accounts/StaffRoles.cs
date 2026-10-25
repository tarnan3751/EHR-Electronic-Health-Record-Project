namespace Ehr.Data.Accounts;

// The roles a staff account can hold. They live in a text[] column on the account rather than in Identity's role
// tables: two values don't need three tables. The check constraint in StaffAccountConfiguration allows only these,
// and sign-in turns each one into a role claim, so authorization policies use RequireRole as usual.
public static class StaffRoles
{
    // Finds patients, reads charts, writes notes, places orders.
    public const string Clinician = "Clinician";

    // Creates staff accounts and reads the audit log.
    public const string Admin = "Admin";

    public static readonly string[] All = [Clinician, Admin];
}
