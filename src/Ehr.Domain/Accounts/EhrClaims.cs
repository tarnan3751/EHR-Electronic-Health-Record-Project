namespace Ehr.Domain.Accounts;

// Claims of our own in the sign-in cookies, beyond Identity's user ID, name and security stamp.
public static class EhrClaims
{
    // A staff account's display name, for the page header and the audit log.
    public const string DisplayName = "ehr:display_name";

    // The patient a portal account belongs to. The portal takes its subject from here, never from a request.
    public const string PatientId = "ehr:patient_id";

    // Every portal account has this one (step 6b): a proxy-only account has no patient of its own, so no PatientId,
    // and is still a portal session. Sessions from before this claim existed all have a PatientId.
    public const string Portal = "ehr:portal";

    public static bool IsPortalAccount(this System.Security.Claims.ClaimsPrincipal user) =>
        user.HasClaim(claim => claim.Type is Portal or PatientId);
}
