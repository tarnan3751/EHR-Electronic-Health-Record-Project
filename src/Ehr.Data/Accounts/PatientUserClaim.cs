using Microsoft.AspNetCore.Identity;

namespace Ehr.Data.Accounts;

// Identity's per-user claims for patient accounts. The app stores none, but the store reads this table on every
// sign-in, so it has to exist. A table of its own, so a patient claim can never point at a staff account.
public class PatientUserClaim : IdentityUserClaim<Guid>;
