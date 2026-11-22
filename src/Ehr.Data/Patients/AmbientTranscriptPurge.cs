using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ehr.Data.Patients;

// Deletes the transcripts of recorded visits whose reviews nobody finished within `keep` (the expansion plan's step 16):
// ehr.purge_ambient_transcripts, which holds `keep` to at most 7 days, runs as the database's system act and refuses
// anyone signed in (db/policies/56-ambient-listening.sql). Only the on-site app's timer calls this, in a transaction of
// its own with nobody signed in. It reads nothing of a record: the count of transcripts deleted comes back.
public static class AmbientTranscriptPurge
{
    public static async Task<long> PurgeAsync(EhrDbContext db, TimeSpan keep, CancellationToken cancel)
    {
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("The purge runs in a database transaction.");
        return await db.Database.GetDbConnection().ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT deleted FROM ehr.purge_ambient_transcripts(@keep)",
            new { keep }, transaction.GetDbTransaction(), cancellationToken: cancel));
    }
}
