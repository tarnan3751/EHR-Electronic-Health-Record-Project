using System.Linq.Expressions;
using Ehr.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ehr.Domain.QuickTexts;

// Saves phrases for the page and the sync API alike: the sync conflict resolver. A save only goes over the version
// it started from, and nothing is merged or guessed: anything else comes back as an outcome, with the phrase as it's
// saved now. Shortcuts are stored lowercase, so .NAD and .nad are the same shortcut. Run it inside the request's
// transaction; each save is its own savepoint, so a refused one leaves the transaction usable.
public sealed class QuickTextSaver(EhrDbContext db)
{
    // id comes from the client: UUIDv7, so a phrase created offline keeps its ID.
    public Task<QuickTextSave> AddAsync(Guid id, QuickTextInput input, Guid? operationKey, CancellationToken cancellationToken)
    {
        var quickText = new QuickText
        {
            Id = id,
            Shortcut = Normalize(input.Shortcut!),
            Body = input.Body!,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.QuickTexts.Add(quickText);
        return SaveAsync(quickText, operationKey, cancellationToken);
    }

    public async Task<QuickTextSave> UpdateAsync(
        Guid id, int fromVersion, QuickTextInput input, Guid? operationKey, CancellationToken cancellationToken)
    {
        var quickText = await db.QuickTexts.FindAsync([id], cancellationToken);
        if (quickText is null)
        {
            return new(QuickTextSaveOutcome.NotFound, null);
        }

        // If someone has saved since fromVersion, the UPDATE matches no row and EF Core throws
        // DbUpdateConcurrencyException.
        db.Entry(quickText).Property(q => q.Version).OriginalValue = fromVersion;
        quickText.Shortcut = Normalize(input.Shortcut!);
        quickText.Body = input.Body!;
        quickText.Version = fromVersion + 1;
        quickText.UpdatedAt = DateTimeOffset.UtcNow;
        return await SaveAsync(quickText, operationKey, cancellationToken);
    }

    // With an operation key (sync pushes), the key is saved together with the change, so the change and the record
    // that it happened commit or roll back as one.
    async Task<QuickTextSave> SaveAsync(QuickText quickText, Guid? operationKey, CancellationToken cancellationToken)
    {
        if (operationKey is { } key)
        {
            db.SyncOperations.Add(new SyncOperation { IdempotencyKey = key, RowId = quickText.Id, AppliedAt = DateTimeOffset.UtcNow });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new(QuickTextSaveOutcome.Saved, quickText);
        }
        catch (DbUpdateConcurrencyException)
        {
            return await RefusedAsync(QuickTextSaveOutcome.ChangedSince, q => q.Id == quickText.Id, cancellationToken);
        }
        catch (DbUpdateException e) when (Violates(e, "ix_quick_texts_shortcut"))
        {
            var shortcut = quickText.Shortcut;
            return await RefusedAsync(QuickTextSaveOutcome.ShortcutTaken, q => q.Shortcut == shortcut, cancellationToken);
        }
        catch (DbUpdateException e) when (Violates(e, "pk_quick_texts"))
        {
            // A new phrase whose ID is saved already: it was created before, so this save is out of date.
            return await RefusedAsync(QuickTextSaveOutcome.ChangedSince, q => q.Id == quickText.Id, cancellationToken);
        }
        catch (DbUpdateException e) when (Violates(e, "pk_sync_operations"))
        {
            // The same operation, sent twice at once: the other copy was applied.
            return await RefusedAsync(QuickTextSaveOutcome.AlreadyApplied, q => q.Id == quickText.Id, cancellationToken);
        }
    }

    // EF Core rolled the save back to its savepoint; forget the refused changes and read what's saved now.
    async Task<QuickTextSave> RefusedAsync(
        QuickTextSaveOutcome outcome, Expression<Func<QuickText, bool>> current,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        return new(outcome, await db.QuickTexts.AsNoTracking().SingleOrDefaultAsync(current, cancellationToken));
    }

    static string Normalize(string shortcut) => shortcut.ToLowerInvariant();

    static bool Violates(DbUpdateException e, string constraint) =>
        e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } violation
        && violation.ConstraintName == constraint;
}

public enum QuickTextSaveOutcome
{
    Saved,

    // Someone saved the phrase since the version this save started from.
    ChangedSince,

    // Another phrase has the shortcut.
    ShortcutTaken,

    // A sync operation applied already, under the same key.
    AlreadyApplied,

    NotFound,
}

// What happened, and the phrase as it's saved now: the one just saved, the newer version, the phrase holding the
// shortcut, or, for NotFound, none.
public sealed record QuickTextSave(QuickTextSaveOutcome Outcome, QuickText? QuickText);
