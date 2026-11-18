using Microsoft.AspNetCore.Identity;

namespace Ehr.Data.Accounts;

// A patient's portal account, separate from staff accounts in every way: table, cookie scheme, sign-in page and
// host. An account belongs to one patient record, or to none: a proxy-only account (the expansion plan's step 6b)
// is someone who uses the portal for a patient without being one, and sees only the records shared with it. The
// portal takes the patient from the session, never from a request. Not under row-level security, because sign-in has to find the account before anyone is
// signed in; the grants in db/policies/10-grants.sql protect it. The email address is the user name, and it is
// confirmed before the account can sign in.
public class PatientUser : IdentityUser<Guid>
{
    // UUIDv7, like every other ID here. Identity leaves a Guid key empty, so it is set on creation.
    public PatientUser() => Id = Guid.CreateVersion7();

    // The patient record this account sees as its own (ehr.patients): one account per patient. Null for a proxy-only
    // account, which has no record of its own.
    public Guid? PatientId { get; set; }

    // What a proxy-only account's holder is called, typed when they accepted their invitation: how the clinic sees
    // who wrote a message for a patient. Null for a patient's account, whose name is the patient's.
    public string? DisplayName { get; set; }
}
