using Ehr.Data.Accounts;
using Ehr.Domain.Accounts;
using Microsoft.AspNetCore.Authorization;

namespace Ehr.Domain.Authorization;

// The authorization policies, attached to whole areas by convention in Program.cs (AGENTS.md): the first layer of
// authorization; row-level security in the database is the second. Each names its cookie scheme, so a patient's
// cookie can never satisfy a clinic policy or the other way round.
public static class EhrPolicies
{
    // Any active staff account: the Clinical area's root, which holds nothing that is PHI.
    public const string Staff = "Staff";

    // Patient data: finding patients, reading charts, and later notes and orders.
    public const string Clinician = "Clinician";

    // Staff accounts and the audit log.
    public const string Admin = "Admin";

    // The portal: a patient account, which always belongs to one patient.
    public const string Patient = "Patient";

    public static AuthorizationOptions AddEhrPolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(Staff, policy => policy
            .AddAuthenticationSchemes(AccountSchemes.Staff)
            .RequireAuthenticatedUser());
        options.AddPolicy(Clinician, policy => policy
            .AddAuthenticationSchemes(AccountSchemes.Staff)
            .RequireAuthenticatedUser()
            .RequireRole(StaffRoles.Clinician));
        options.AddPolicy(Admin, policy => policy
            .AddAuthenticationSchemes(AccountSchemes.Staff)
            .RequireAuthenticatedUser()
            .RequireRole(StaffRoles.Admin));
        options.AddPolicy(Patient, policy => policy
            .AddAuthenticationSchemes(AccountSchemes.Patient)
            .RequireAuthenticatedUser()
            .RequireClaim(EhrClaims.PatientId));

        // Nothing is anonymous unless it says so: an endpoint with no policy of its own still needs a signed-in user.
        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        return options;
    }
}
