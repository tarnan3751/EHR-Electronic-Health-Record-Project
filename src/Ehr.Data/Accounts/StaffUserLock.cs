using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ehr.Data.Accounts;

// A staff account's second factors and passkey changes, one request at a time (the expansion plan's step 19's final
// review): the account's row is held until the request's transaction ends, and the account read again under it. Without
// it, requests racing on one account each counted a wrong code against the account as they had read it, and all but one
// failed on its concurrency stamp and were rolled back, the failure uncounted; and two finished adds could both pass the
// limit on passkeys. Taken before anything else in the request touches the account.
public static class StaffUserLock
{
    public static async Task AcquireAsync(EhrDbContext db, StaffUser user, CancellationToken cancellationToken)
    {
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Holding a staff account needs the request's database transaction.");
        await db.Database.GetDbConnection().ExecuteAsync(new CommandDefinition(
            "SELECT 1 FROM ehr.staff_users WHERE id = @id FOR UPDATE",
            new { id = user.Id }, transaction.GetDbTransaction(), cancellationToken: cancellationToken));
        await db.Entry(user).ReloadAsync(cancellationToken);
    }
}
