namespace Ehr.Domain.Accounts;

// Claims of our own in the sign-in cookies, beyond Identity's user ID, name and security stamp.
public static class EhrClaims
{
    // A staff account's display name, for the page header and the audit log.
    public const string DisplayName = "ehr:display_name";

    // The patient a portal account belongs to. The portal takes its subject from here, never from a request.
    public const string PatientId = "ehr:patient_id";
}
