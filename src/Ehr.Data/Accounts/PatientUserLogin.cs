using Microsoft.AspNetCore.Identity;

namespace Ehr.Data.Accounts;

// Identity's external logins for patient accounts. Unused: there are no external identity providers. Mapped
// because the store expects the table to exist.
public class PatientUserLogin : IdentityUserLogin<Guid>;
