using Microsoft.AspNetCore.Http;

namespace Ehr.Domain.Accounts;

// The authentication schemes: one cookie per kind of account, so a staff cookie is never read as a patient's or the
// other way round, plus the short-lived cookie that carries a staff sign-in from its password step to its second
// factor. The default scheme picks Staff or Patient by path, so every request has one principal for the middleware
// to read; the authorization policies name their scheme explicitly as well (EhrPolicies).
public static class AccountSchemes
{
    public const string Staff = "Staff";

    public const string Patient = "Patient";

    public const string StaffPending = "StaffPending";

    // The default, which forwards to Staff or Patient.
    public const string Default = "Ehr";

    // Everything the patient portal serves lives under this path; everything else is the clinic's.
    public const string PortalPath = "/portal";

    public static string Select(HttpContext context) =>
        context.Request.Path.StartsWithSegments(PortalPath) ? Patient : Staff;
}
