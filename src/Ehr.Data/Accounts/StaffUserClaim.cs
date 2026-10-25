using Microsoft.AspNetCore.Identity;

namespace Ehr.Data.Accounts;

// Identity's per-user claims for staff accounts. The app stores none (roles are a column on the account), but the
// store reads this table on every sign-in, so it has to exist. A table of its own, so a staff claim can never
// point at a patient account.
public class StaffUserClaim : IdentityUserClaim<Guid>;
