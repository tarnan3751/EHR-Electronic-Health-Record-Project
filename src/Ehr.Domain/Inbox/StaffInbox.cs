using Ehr.Data;
using Ehr.Data.Inbox;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ehr.Domain.Inbox;

// The staff inbox's one write: marking an item done (the expansion plan's step 3). Called inside the page's database
// transaction. The items are read through PhiReader. Whose item it is, and that it is marked once and nothing else
// about it changes, are the database's rules (db/policies/30-rls.sql, 70-inbox.sql).
public sealed class StaffInbox(EhrDbContext db)
{
    // False when the item isn't this person's, or was done already; nothing is saved then.
    public async Task<bool> CompleteAsync(Guid id, CancellationToken cancellationToken)
    {
        // No such item. (A stand-in with an empty key would be taken for a new row.)
        if (id == Guid.Empty)
        {
            return false;
        }

        const string savepoint = "inbox_item";
        var transaction = db.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync(savepoint, cancellationToken);
        // A stand-in for the row, with its key: only done_at is sent, and the database sets its value.
        var item = new InboxItem { Id = id, Kind = "", Summary = "" };
        var entry = db.Attach((object)item);
        item.DoneAt = DateTimeOffset.UtcNow;
        entry.Property(nameof(InboxItem.DoneAt)).IsModified = true;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.ReleaseSavepointAsync(savepoint, cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return false;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.CheckViolation })
        {
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return false;
        }
        finally
        {
            entry.State = EntityState.Detached;
        }
    }
}
