using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ehr.Data.Orders;

// One thing known against ordering a drug for a patient (the expansion plan's step 8), as the database's function
// ehr.order_safety_warnings gives it (db/policies/61-order-safety.sql): the same function the signing trigger asks, so
// what the order form shows is what the database will require a reason for.
//   Kind      "allergy", "allergy-class" or "interaction".
//   Severity  "major", "moderate", "minor" or "high-priority".
//   Ordered   the ingredient of the ordered drug it is about.
//   Against   the allergy on the chart, or the drug the patient takes.
//   Source    "chart", "fda-class", "cross-reactivity", or an interaction's source: "ddinter", "onc", "fda-label".
//   Detail    what the source says, when it says something; Citation where to check it.
public sealed record SafetyWarning(string Kind, string Severity, string Ordered, string Against, string Source, string? Detail, string? Citation);

// Asks the database. Raw SQL, so it lives here (AGENTS.md); it runs on the context's connection and in its
// transaction, as the signed-in user, and every argument is a parameter.
public static class OrderSafety
{
    public static async Task<List<SafetyWarning>> WarningsAsync(EhrDbContext db, Guid patient, string catalogCode, Guid? replaces, CancellationToken cancellationToken)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        // against_sensitive is for what the database keeps on the order; the clinician ordering sees the name.
        command.CommandText = "SELECT kind, severity, ordered, against, source, detail, citation FROM ehr.order_safety_warnings(@patient, @code, @replaces)";
        foreach (var (name, value) in new (string, object)[] { ("patient", patient), ("code", catalogCode), ("replaces", (object?)replaces ?? DBNull.Value) })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            if (name == "replaces")
            {
                parameter.DbType = System.Data.DbType.Guid;
            }

            command.Parameters.Add(parameter);
        }

        var warnings = new List<SafetyWarning>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            warnings.Add(new SafetyWarning(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return warnings;
    }
}
