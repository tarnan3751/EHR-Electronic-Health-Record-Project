using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ehr.Data.Proxies;

// One row of ehr.proxied_records() (db/policies/30-rls.sql): a record the signed-in portal account may open as a
// proxy, with the patient's name and nothing else of theirs. The function answers only for the caller's own address.
public sealed record ProxiedRecordRow(Guid GrantId, Guid PatientId, string GivenName, string FamilyName, string Scope, bool IncludeSensitive, DateTimeOffset ExpiresAt);

// Reads the function. Raw SQL, so it lives here (AGENTS.md); it runs in the caller's transaction, where the user
// context is set, and takes no input.
public static class ProxiedRecords
{
    public static async Task<List<ProxiedRecordRow>> ListAsync(EhrDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT grant_id, patient_id, given_name, family_name, scope, include_sensitive, expires_at FROM ehr.proxied_records()";
        var rows = new List<ProxiedRecordRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ProxiedRecordRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetBoolean(5), reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return rows;
    }
}
