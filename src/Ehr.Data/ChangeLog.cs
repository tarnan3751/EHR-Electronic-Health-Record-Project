using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Data;

// Reads for sync pulls, by transaction ID, never by timestamp: transactions commit out of order, so a timestamp
// bookmark can skip a change for good. Run both reads in one REPEATABLE READ transaction, so they share a snapshot.
public static class ChangeLog
{
    // The next pull's bookmark: every transaction with a lower ID has finished, so its changes are visible to this
    // snapshot. Anything still running, or started later, has an ID at or above it.
    public static async Task<ulong> WatermarkAsync(EhrDbContext db, CancellationToken cancellationToken) =>
        ulong.Parse(
            await db.Database.SqlQuery<string>($"SELECT pg_snapshot_xmin(pg_current_snapshot())::text AS \"Value\"")
                .SingleAsync(cancellationToken),
            CultureInfo.InvariantCulture);

    // The quick texts that transactions with IDs from `from` up to (not including) `to` inserted or updated, as
    // they are now.
    public static IQueryable<QuickText> QuickTextsChanged(EhrDbContext db, ulong from, ulong to) =>
        db.QuickTexts.FromSql(
            $"""
            SELECT * FROM ehr.quick_texts WHERE id IN (
                SELECT row_id FROM ehr.change_log
                WHERE table_name = 'quick_texts'
                  AND transaction_id >= {from.ToString(CultureInfo.InvariantCulture)}::xid8
                  AND transaction_id < {to.ToString(CultureInfo.InvariantCulture)}::xid8)
            """);
}
