using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Ehr.Data;

// Positions in PostgreSQL's write-ahead log (WAL), which the cloud replica replays to stay a copy of the primary.
// The cloud app uses them so people see their own saves: after a save, it sends their reads to the on-prem app
// until its replica has replayed past the save.
public static class WalPosition
{
    // Positions look like 0/3000148.
    public static bool IsValid(string position) => NpgsqlLogSequenceNumber.TryParse(position, out _);

    // On the primary: how far the WAL has got. Read just after a commit, it is at or past that commit.
    public static Task<string> OfPrimaryAsync(EhrDbContext db, CancellationToken cancellationToken) =>
        db.Database.SqlQuery<string>($"SELECT pg_current_wal_lsn()::text AS \"Value\"")
            .SingleAsync(cancellationToken);

    // Whether this database has everything up to position: a replica once it has replayed that far, a primary once
    // its WAL has got there.
    public static Task<bool> HasReachedAsync(EhrDbContext db, string position, CancellationToken cancellationToken) =>
        db.Database.SqlQuery<bool>(
            $"""
            SELECT coalesce(
                CASE WHEN pg_is_in_recovery() THEN pg_last_wal_replay_lsn() ELSE pg_current_wal_lsn() END >= {position}::pg_lsn,
                false) AS "Value"
            """)
            .SingleAsync(cancellationToken);
}
