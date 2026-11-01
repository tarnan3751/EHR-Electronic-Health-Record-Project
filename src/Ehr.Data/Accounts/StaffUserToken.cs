using Microsoft.AspNetCore.Identity;

namespace Ehr.Data.Accounts;

// Identity's per-user tokens for staff accounts: the TOTP authenticator key and the recovery codes. Like every
// table, it replicates to the cloud host; the threat model records that.
public class StaffUserToken : IdentityUserToken<Guid>;
