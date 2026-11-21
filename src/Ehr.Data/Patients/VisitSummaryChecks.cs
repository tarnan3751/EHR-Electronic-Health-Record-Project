using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ehr.Data.Patients;

// One thing ehr.visit_summary_problems (db/policies/52-visit-summaries.sql) found in a visit summary: the part it is in
// (1 What we talked about, 2 What we found, 3 What happens next), what was found, and its kind.
public sealed record VisitSummaryProblem(int Part, string Found, string Kind);

// Asks the database about a visit summary (db/policies/52-visit-summaries.sql): its rule about the text, before it is
// released, so the page can say what to change (the same rule refuses the row when it is inserted); and, once the caller
// has withdrawn one, whether it had been viewed in the portal, so the patient can be told. Raw SQL, so it lives here
// (AGENTS.md); it runs in the caller's transaction, where the user context is set.
public static class VisitSummaryChecks
{
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
