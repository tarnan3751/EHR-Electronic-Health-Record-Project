namespace Ehr.Data;

// Who the current unit of work runs for: one per request. UserContextInterceptor hands it to PostgreSQL at the
// start of every transaction, where row-level security reads it.
public sealed class UserContext
{
    // Null while nobody is signed in, which is always for now: sign-in doesn't exist yet.
    public Guid? UserId { get; set; }
}
