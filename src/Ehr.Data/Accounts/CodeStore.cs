using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ehr.Data.Accounts;

// The apps' only way to enrollment codes and proxy invitations (the expansion plan's step 6c): the functions in
// db/policies/45-codes.sql, one code at a time. The runtime roles have no privilege on the tables themselves, so
// nothing here, or anywhere, can list them. Raw SQL, so it lives in Ehr.Data (AGENTS.md); it runs on the context's
// connection and in its transaction. Every argument is a parameter.
public static class CodeStore
{
    public static Task IssueEnrollmentAsync(EhrDbContext db, Guid id, Guid patient, string hash, CancellationToken cancellationToken) =>
        ExecuteAsync(db, "SELECT ehr.issue_patient_enrollment(@id, @patient, @hash)", cancellationToken, ("id", id), ("patient", patient), ("hash", hash));

    // Uses the code up; null when it was unknown, expired, or used a moment ago.
    public static Task<(Guid Id, Guid PatientId)?> RedeemEnrollmentAsync(EhrDbContext db, string hash, string legacy, CancellationToken cancellationToken) =>
        OneAsync(db, "SELECT id, patient_id FROM ehr.redeem_patient_enrollment(@hash, @legacy)", reader => (reader.GetGuid(0), reader.GetGuid(1)), cancellationToken,
            ("hash", hash), ("legacy", legacy));

    public static Task IssueInvitationAsync(EhrDbContext db, Guid id, Guid grant, string hash, CancellationToken cancellationToken) =>
        ExecuteAsync(db, "SELECT ehr.issue_proxy_invitation(@id, @grant, @hash)", cancellationToken, ("id", id), ("grant", grant), ("hash", hash));

    // The address an unused, unexpired invitation was sent to, or null.
    public static Task<(Guid Id, string Email)?> FindInvitationAsync(EhrDbContext db, string hash, CancellationToken cancellationToken) =>
        OneAsync(db, "SELECT id, email FROM ehr.find_proxy_invitation(@hash)", reader => (reader.GetGuid(0), reader.GetString(1)), cancellationToken, ("hash", hash));

    public static Task<(Guid Id, string Email)?> RedeemInvitationAsync(EhrDbContext db, string hash, CancellationToken cancellationToken) =>
        OneAsync(db, "SELECT id, email FROM ehr.redeem_proxy_invitation(@hash)", reader => (reader.GetGuid(0), reader.GetString(1)), cancellationToken, ("hash", hash));

    static async Task ExecuteAsync(EhrDbContext db, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(db, sql, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    static async Task<T?> OneAsync<T>(EhrDbContext db, string sql, Func<DbDataReader, T> read, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
        where T : struct
    {
        await using var command = Command(db, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? read(reader) : null;
    }

    static DbCommand Command(EhrDbContext db, string sql, (string Name, object Value)[] parameters)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return command;
    }
}
