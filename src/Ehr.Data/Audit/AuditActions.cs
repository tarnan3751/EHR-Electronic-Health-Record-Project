namespace Ehr.Data.Audit;

// The values AuditEvent.Action may hold. Plain text in the database, so adding one is a code change, not a migration.
public static class AuditActions
{
    // A PHI read, recorded by the PHI reader.
    public const string Read = "read";

    // Writes to a PHI table, recorded by its trigger.
    public const string Insert = "insert";

    public const string Update = "update";

    public const string Delete = "delete";

    public const string SignIn = "sign_in";

    public const string SignInFailed = "sign_in_failed";

    public const string SignOut = "sign_out";

    // Emergency access to a patient outside the actor's normal access, with its stated reason.
    public const string BreakGlass = "break_glass";

    // The first admin account, created from configuration when there were no staff accounts.
    public const string BootstrapAdmin = "bootstrap_admin";
}
