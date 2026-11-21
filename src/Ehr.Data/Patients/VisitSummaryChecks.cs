using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ehr.Data.Patients;

// A withdrawn visit summary as the portal is given it (ehr.withdrawn_visit_summaries): the date of the note it was
// written from and whether it is sensitive, and nothing else of it.
public sealed record WithdrawnVisitSummary(DateTimeOffset NoteSignedAt, bool Sensitive);

// One thing ehr.visit_summary_problems (db/policies/52-visit-summaries.sql) found in a visit summary: the part it is in
// (1 What we talked about, 2 What we found, 3 What happens next), what was found, and its kind.
public sealed record VisitSummaryProblem(int Part, string Found, string Kind);

// Asks the database about visit summaries (db/policies/52-visit-summaries.sql): its rule about the text, before one is
// released, so the page can say what to change (the same rule refuses the row when it is inserted); once the caller
// has withdrawn one, whether it had been viewed in the portal, so the patient can be told; and, for the portal, the
// dates of the withdrawn ones the session may know of. Raw SQL, so it lives here (AGENTS.md); it runs in the caller's
// transaction, where the user context is set.
public static class VisitSummaryChecks
{
    // The withdrawn summaries of the record the portal session is looking at, newest first, under the rule a standing
    // one is read by: their dates and whether each is sensitive.
    public static async Task<List<WithdrawnVisitSummary>> WithdrawnAsync(EhrDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT note_signed_at, sensitive FROM ehr.withdrawn_visit_summaries()";
        var rows = new List<WithdrawnVisitSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new WithdrawnVisitSummary(reader.GetFieldValue<DateTimeOffset>(0), reader.GetBoolean(1)));
        }

        return rows;
    }

    // Whether the summary the caller withdrew in this transaction had been viewed in the portal, by the patient or a
    // proxy (ehr.withdrawn_summary_was_viewed). False for any other summary.
    public static async Task<bool> WithdrawnWasViewedAsync(EhrDbContext db, Guid summary, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT ehr.withdrawn_summary_was_viewed(@summary)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "summary";
        parameter.Value = summary;
        command.Parameters.Add(parameter);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    public static async Task<List<VisitSummaryProblem>> ProblemsAsync(
        EhrDbContext db, Guid patient, Guid note, string talkedAbout, string found, string nextSteps, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT part, found, kind FROM ehr.visit_summary_problems(@patient, @note, ARRAY[@talked_about, @found, @next_steps])";
        foreach (var (name, value) in new (string, object)[] { ("patient", patient), ("note", note), ("talked_about", talkedAbout), ("found", found), ("next_steps", nextSteps) })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        var problems = new List<VisitSummaryProblem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            problems.Add(new VisitSummaryProblem(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }

        return problems;
    }
}
