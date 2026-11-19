namespace Ehr.Data.Audit;

// The values AuditEvent.Action may hold. Plain text in the database, so adding one is a code change, not a migration.
public static class AuditActions
{
    // A PHI read, recorded by the PHI reader.
    public const string Read = "read";

    // A read that showed a patient's sensitive items (diagnoses and medications marked sensitive), which a chart
    // leaves out until asked: recorded by the PHI reader in place of the plain read, so one view is one event.
    public const string ReadSensitive = "read_sensitive";

    // Writes to a PHI table, recorded by its trigger.
    public const string Insert = "insert";

    public const string Update = "update";

    public const string Delete = "delete";

    public const string AiReviewStaged = "ai_review_staged";
    public const string AiFieldReviewed = "ai_field_reviewed";
    public const string AiReviewCompleted = "ai_review_completed";

    public const string SignNote = "sign_note";

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

    // A patient whose confirmation link expired or got lost asked for a new one, and it was sent.
    public const string ConfirmationResent = "confirmation_resent";

    // Someone accepted an invitation and created a proxy-only portal account (step 6b). No patient: the account has
    // no record of its own. The actor is the new account.
    public const string ProxyAccountCreated = "proxy_account_created";

    // A patient whose account was never confirmed (a mistyped address, say) registered again with a new code: the
    // account's address and password were replaced.
    public const string RegistrationReplaced = "registration_replaced";

    // Every action, for the audit page's filter.
    public static readonly string[] All =
    [
        Read, ReadSensitive, Insert, Update, Delete, SignNote, AiReviewStaged, AiFieldReviewed, AiReviewCompleted, SignIn, SignInFailed, SignOut, BreakGlass, BootstrapAdmin, PasswordChanged,
        PasswordResetRequested, PasswordReset, AuthenticatorReset, AccountDisabled, AccountEnabled, MfaEnrolled,
        RecoveryCodeUsed, StaffInvited, EnrollmentIssued, PatientRegistered, EmailConfirmed, ConfirmationResent,
        RegistrationReplaced, ProxyAccountCreated,
    ];
}
