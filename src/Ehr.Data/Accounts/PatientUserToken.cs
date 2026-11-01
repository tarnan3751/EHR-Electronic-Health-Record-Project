using Microsoft.AspNetCore.Identity;

namespace Ehr.Data.Accounts;

// Identity's per-user tokens for patient accounts: the email confirmation and password reset tokens, and the TOTP
// key if a patient turns MFA on. Like every table, it replicates to the cloud host.
public class PatientUserToken : IdentityUserToken<Guid>;
