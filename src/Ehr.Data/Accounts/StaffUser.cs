using Microsoft.AspNetCore.Identity;

namespace Ehr.Data.Accounts;

// A staff account: clinicians and administrators. Staff and patient accounts are separate tables, separate cookie
// schemes and separate sign-in pages, so one can never be taken for the other. Not PHI, and not under row-level
// security: sign-in has to find the account before anyone is signed in. The grants in db/policies/10-grants.sql
// protect it. The email address is the user name.
public class StaffUser : IdentityUser<Guid>
{
    // UUIDv7, like every other ID here. Identity leaves a Guid key empty, so it is set on creation.
    public StaffUser() => Id = Guid.CreateVersion7();

    // Shown in the app and written into audit events.
    public required string DisplayName { get; set; }

    // One or more of StaffRoles. The database refuses any other value.
    public string[] Roles { get; set; } = [];

    // Set on the bootstrap admin, whose first password came from configuration. Sign-in forces a new password and
    // MFA enrollment before anything else.
    public bool MustChangePassword { get; set; }
}
