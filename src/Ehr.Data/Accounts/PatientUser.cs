using Microsoft.AspNetCore.Identity;

namespace Ehr.Data.Accounts;

// A patient's portal account, separate from staff accounts in every way: table, cookie scheme, sign-in page and
// host. Each account belongs to exactly one patient record, and the portal takes the patient from the session,
// never from a request. Not under row-level security, because sign-in has to find the account before anyone is
// signed in; the grants in db/policies/10-grants.sql protect it. The email address is the user name, and it is
// confirmed before the account can sign in.
public class PatientUser : IdentityUser<Guid>
{
    // UUIDv7, like every other ID here. Identity leaves a Guid key empty, so it is set on creation.
    public PatientUser() => Id = Guid.CreateVersion7();

    // The patient record this account sees (ehr.patients). One account per patient.
    public Guid PatientId { get; set; }
}
