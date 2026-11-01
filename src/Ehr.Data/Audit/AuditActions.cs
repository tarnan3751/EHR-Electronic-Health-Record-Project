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

    public const string PasswordChanged = "password_changed";

    // A patient asked for a password reset link; an admin reset a staff password, so the old one stopped working and
    // a new setup link was issued.
    public const string PasswordResetRequested = "password_reset_requested";

    public const string PasswordReset = "password_reset";

    // An admin reset a staff account's authenticator, disabled the account, or enabled it again.
    public const string AuthenticatorReset = "authenticator_reset";

    public const string AccountDisabled = "account_disabled";

    public const string AccountEnabled = "account_enabled";

    // A staff account enrolled an authenticator app, or used one of its recovery codes.
    public const string MfaEnrolled = "mfa_enrolled";

    public const string RecoveryCodeUsed = "recovery_code_used";

    // An admin created a staff account and its setup link.
    public const string StaffInvited = "staff_invited";

    // A clinician issued a patient a portal enrollment code.
    public const string EnrollmentIssued = "enrollment_issued";

    public const string PatientRegistered = "patient_registered";

    public const string EmailConfirmed = "email_confirmed";

    // Every action, for the audit page's filter.
    public static readonly string[] All =
    [
        Read, Insert, Update, Delete, SignIn, SignInFailed, SignOut, BreakGlass, BootstrapAdmin, PasswordChanged,
        PasswordResetRequested, PasswordReset, AuthenticatorReset, AccountDisabled, AccountEnabled, MfaEnrolled,
        RecoveryCodeUsed, StaffInvited, EnrollmentIssued, PatientRegistered, EmailConfirmed,
    ];
}
