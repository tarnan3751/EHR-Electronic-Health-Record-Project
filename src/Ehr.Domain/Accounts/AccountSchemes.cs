using Microsoft.AspNetCore.Http;

namespace Ehr.Domain.Accounts;

// The authentication schemes: one cookie per kind of account, so a staff cookie is never read as a patient's or the
// other way round; the short-lived cookie that carries a staff sign-in from its password step to its second factor;
// and the desktop app's session token (DesktopTokenHandler). The default scheme picks one by path, so every request
// has one principal for the middleware to read; the authorization policies name their scheme explicitly as well
// (EhrPolicies).
public static class AccountSchemes
{
    public const string Staff = "Staff";

    public const string Patient = "Patient";

    public const string StaffPending = "StaffPending";

    // A staff account signed in from the desktop app: a bearer token, never a cookie.
    public const string Desktop = "Desktop";

    // The default, which forwards to Staff, Patient or Desktop.
    public const string Default = "Ehr";

    // Everything the patient portal serves lives under this path; the APIs the desktop app calls under the other; and
    // everything else is the clinic's.
    public const string PortalPath = "/portal";

    public const string ApiPath = "/api";

    public static string Select(HttpContext context) =>
        context.Request.Path.StartsWithSegments(PortalPath) ? Patient
        : context.Request.Path.StartsWithSegments(ApiPath) ? Desktop
        : Staff;
}
