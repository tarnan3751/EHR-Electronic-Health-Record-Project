namespace Ehr.Data;

// Who the current unit of work runs for: one per request. UserContextInterceptor hands it to PostgreSQL at the
// start of every transaction, where row-level security reads it.
public sealed class UserContext
{
    // Null while nobody is signed in, which is always for now: sign-in doesn't exist yet.
    public Guid? UserId { get; set; }

    // The proxy grant a portal session is acting under (FR-19), or null when the account is looking at its own record.
    // The database, not this, decides whether the grant is the account's and still in force: one that isn't opens
    // nothing at all (ehr.current_patient_id in db/policies/30-rls.sql).
    public Guid? ActingGrant { get; set; }
}
