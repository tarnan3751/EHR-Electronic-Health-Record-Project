using System.Text.RegularExpressions;
using Ehr.Testing;
using Xunit;

namespace Ehr.Security.Tests;

// Every policy calls the user-context functions as scalar subqueries, (SELECT ehr.is_clinician()), so PostgreSQL runs
// each once per statement (db/policies/30-rls.sql). Read from the catalog, so the USING and WITH CHECK clauses of
// INSERT and UPDATE policies count too, which no SELECT plan shows. PostgreSQL deparses such a call as exactly
// "( SELECT ehr.is_clinician() AS is_clinician)": a call of any function in ehr in any other form, bare or in a
// subquery that reads the row, runs once per row, and fails the test.
public partial class PolicyDefinitionTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    [Fact]
    public async Task Every_policy_asks_who_the_user_is_through_a_scalar_subquery()
    {
        var clauses = await Accounts.QueryAsync(database.SuperuserConnectionString, null,
            """
            SELECT c.relname || '.' || p.polname || ' ' || clause.kind || ': ' || clause.expression
            FROM pg_policy p
            JOIN pg_class c ON c.oid = p.polrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            CROSS JOIN LATERAL (VALUES ('USING', pg_get_expr(p.polqual, p.polrelid)),
                                       ('WITH CHECK', pg_get_expr(p.polwithcheck, p.polrelid))) AS clause(kind, expression)
            WHERE n.nspname = 'ehr' AND clause.expression IS NOT NULL
            """);

        // What a wrapped call looks like, so the check below can't pass by reading nothing.
        Assert.Contains(clauses, clause => clause.Contains("( SELECT ehr.is_clinician() AS is_clinician)", StringComparison.Ordinal));
        Assert.Contains(clauses, clause => clause.Contains("( SELECT ehr.current_user_id() AS current_user_id)", StringComparison.Ordinal));
        var perRow = clauses.Where(clause => AnyCall().IsMatch(OncePerStatement().Replace(clause, ""))).ToList();
        Assert.True(perRow.Count == 0, "These policies call a function in ehr other than once per statement: " + string.Join("; ", perRow));
    }

    // A call with no arguments, alone in a scalar subquery, as PostgreSQL deparses it.
    [GeneratedRegex(@"\( SELECT ehr\.(\w+)\(\) AS \1\)")]
    private static partial Regex OncePerStatement();

    // Any call of a function in ehr.
    [GeneratedRegex(@"ehr\.\w+\s*\(")]
    private static partial Regex AnyCall();
}
