using Microsoft.AspNetCore.Identity;

namespace Ehr.Data.Accounts;

// A staff account's passkey (the expansion plan's step 19): a second factor in place of the authenticator code, never
// a sign-in on its own. Identity's own shape, keyed by the credential's ID, with the public key, the name its owner gave
// it, its signature count and what the authenticator said of it at registration in one JSON column. Nothing in it is a
// secret: the private key never leaves the authenticator. Patient accounts have no passkeys, and no table for them.
public class StaffUserPasskey : IdentityUserPasskey<Guid>;
