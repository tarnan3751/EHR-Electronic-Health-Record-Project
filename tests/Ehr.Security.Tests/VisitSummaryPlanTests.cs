using System.Text;
using System.Text.RegularExpressions;
using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// The visit summaries' rule about codes (ehr.visit_summary_problems, db/policies/52-visit-summaries.sql) looks each word
// of a code's shape up in the code tables by index, whatever the planner's statistics say. It once joined the words to
// the tables in a way the planner answered by reading them whole, for each release with such a word: releasing a
// summary with twenty words like "B12" took most of a second, while the note's lock was held. Here, in a database of
// its own, the rule is run on the longest summary there can be, three parts of 4,000 characters, every one of its words
// of a code's shape and none a code, as the app's role with a clinician in context; the plan of each statement it
// runs, its own query included, is logged (auto_explain, as notices), with the code tables' statistics cleared, as after
// a load nobody analyzed, and then analyzed, and with a plan made for the values and one made for any. None may read a
// whole code table. As in FhirPlanTests (Ehr.Web.Tests), the plan is checked rather than a time, so the test holds on
// any machine however busy; it looks for sequential scans and nothing else. And the order catalog has nothing the
// rule's codes alone wouldn't find (the rule's comment says why it relies on that).
public class VisitSummaryPlanTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    static readonly string[] CodeTables = ["clinical_codes", "observation_codes", "order_catalog_items"];

    [Fact]
    public async Task The_code_rule_looks_each_word_up_by_index_with_the_statistics_cleared_or_gathered()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, patient);
        var (draft, note) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var app = database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);
        // The note's sections are as long as a summary's parts, Subjective and Objective (step 14) of words of a code's
        // shape too: the rule compares a summary with them whole, and looks none of their words up.
        await Accounts.ExecuteAsync(app, clinician,
            """
            INSERT INTO ehr.clinical_drafts
                (id, patient_id, clinician_id, body, version, subjective, objective, assessment, plan, structured_version, has_unreviewed_proposals)
            VALUES (@id, @patient, @owner, 'Synthetic plain text', 1, @subjective, @objective, 'Synthetic assessment.', 'Synthetic plan.', 1, false)
            """, ("id", draft), ("patient", patient), ("owner", clinician), ("subjective", Worst(3)), ("objective", Worst(4)));
        await Accounts.ExecuteAsync(app, clinician,
            "INSERT INTO ehr.clinical_notes (id, draft_id, patient_id, draft_version, subjective, objective, assessment, plan) VALUES (@note, @draft, @patient, 1, @subjective, @objective, 'Synthetic assessment.', 'Synthetic plan.')",
            ("note", note), ("draft", draft), ("patient", patient), ("subjective", Worst(3)), ("objective", Worst(4)));
        // And a note signed before step 14, with its History.
        var (_, earlier) = await EarlierNotes.SignedAsync(database, clinician, patient, Worst(5));
        string[] parts = [Worst(0), Worst(1), Worst(2)];
        Assert.All(parts, part => Assert.Equal(4000, part.Length));

        // As never analyzed: PostgreSQL 18 clears a table's statistics and its columns'.
        var tables = string.Join(", ", CodeTables.Select(table => $"'{table}'"));
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            $"""
            SELECT pg_clear_relation_stats('ehr', t) FROM unnest(ARRAY[{tables}]) t;
            SELECT pg_clear_attribute_stats('ehr', c.relname, a.attname, false)
            FROM pg_class c JOIN pg_attribute a ON a.attrelid = c.oid
            WHERE c.relnamespace = 'ehr'::regnamespace AND c.relname = ANY (ARRAY[{tables}]) AND a.attnum > 0 AND NOT a.attisdropped;
            """);
        await AssertProbedAsync(clinician, patient, note, parts, "with the statistics cleared");
        await AssertProbedAsync(clinician, patient, earlier, parts, "with the statistics cleared, for a note with a History");

        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null, $"ANALYZE {string.Join(", ", CodeTables.Select(table => $"ehr.{table}"))}");
        await AssertProbedAsync(clinician, patient, note, parts, "analyzed");
        await AssertProbedAsync(clinician, patient, earlier, parts, "analyzed, for a note with a History");
    }

    // The rule's lab codes are the observation codes and the catalog's codes: every LOINC code a catalog item stands
    // for (its standard_code, which has no index) is one of them, so looking it up there would find nothing more.
    [Fact]
    public async Task Every_LOINC_code_the_order_catalog_stands_for_is_a_code_the_rule_looks_up()
    {
        Assert.Empty(await Accounts.QueryAsync(database.SuperuserConnectionString, null,
            """
            SELECT i.code || ' ' || i.standard_code FROM ehr.order_catalog_items i
            WHERE i.standard_system = 'loinc'
              AND NOT EXISTS (SELECT 1 FROM ehr.order_catalog_items c WHERE c.code = i.standard_code)
              AND NOT EXISTS (SELECT 1 FROM ehr.observation_codes o WHERE o.code = i.standard_code)
            """));
        // The common items do stand for LOINC codes, so the check has something to check.
        Assert.NotEmpty(await Accounts.QueryAsync(database.SuperuserConnectionString, null,
            "SELECT code FROM ehr.order_catalog_items WHERE standard_system = 'loinc' AND code <> standard_code"));
    }

    // 4,000 characters of words of each code's shape, none of them a code and no two alike: a diagnosis's with its dot
    // and without ("A1c.4", "B2d": no ICD-10-CM code has a third letter after B), a medication's and a lab test's (seven
    // digits, more than either system's codes have), a substance's (ten letters and digits) and an order's ("SYN-ZZ1").
    static string Worst(int part)
    {
        var text = new StringBuilder();
        for (var i = 0; ; i++)
        {
            var n = part * 100_000 + i;
            var diagnosis = $"{(char)('A' + n % 26)}{n % 9 + 1}{(char)('c' + n / 26 % 24)}";
            var word = (i % 6) switch
            {
                0 => diagnosis,
                1 => $"{diagnosis}.{n % 10}",
                2 => $"9{n % 1_000_000:D6}",
                3 => $"9{n % 1_000_000:D6}-{n % 10}",
                4 => $"QZ{n:D8}",
                _ => $"SYN-ZZ{n}",
            };
            if (text.Length + word.Length + 1 > 4000)
            {
                return text.ToString().PadRight(4000, '.');
            }

            text.Append(word).Append(' ');
        }
    }

    // Runs the rule as the app's role, a clinician in context, once with a plan made for the values given and once with
    // one made for any values; every plan logged must read no code table whole, and the rule's own must probe each.
    async Task AssertProbedAsync(Guid clinician, Guid patient, Guid note, string[] parts, string statistics)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        var plans = new List<string>();
        connection.Notice += (_, e) => plans.Add(e.Notice.MessageText);
        await connection.OpenAsync(Cancel);
        await using (var setup = new NpgsqlCommand(
            """
            LOAD 'auto_explain';
            SET auto_explain.log_min_duration = 0;
            SET auto_explain.log_nested_statements = on;
            SET auto_explain.log_level = notice;
            SET ROLE ehr_app;
            """, connection))
        {
            await setup.ExecuteNonQueryAsync(Cancel);
        }

        foreach (var mode in new[] { "force_custom_plan", "force_generic_plan" })
        {
            plans.Clear();
            await using var transaction = await connection.BeginTransactionAsync(Cancel);
            await using (var user = new NpgsqlCommand($"SELECT set_config('app.user_id', @user, true), set_config('plan_cache_mode', '{mode}', true)", connection, transaction))
            {
                user.Parameters.AddWithValue("user", clinician.ToString());
                await user.ExecuteNonQueryAsync(Cancel);
            }

            await using (var rule = new NpgsqlCommand(
                "SELECT coalesce(string_agg(part || ' ' || found || ' ' || kind, ', '), '') FROM ehr.visit_summary_problems(@patient, @note, @parts)", connection, transaction))
            {
                rule.Parameters.AddWithValue("patient", patient);
                rule.Parameters.AddWithValue("note", note);
                rule.Parameters.AddWithValue("parts", parts);
                Assert.Equal("", await rule.ExecuteScalarAsync(Cancel));
            }

            await transaction.RollbackAsync(Cancel);

            var scans = plans.SelectMany(plan => Regex.Matches(plan, @"Seq Scan on (\w+)").Select(scan => (Table: scan.Groups[1].Value, Plan: plan)))
                .Where(scan => CodeTables.Contains(scan.Table)).ToList();
            Assert.True(scans.Count == 0, $"Read the whole of {string.Join(", ", scans.Select(scan => scan.Table).Distinct())} {statistics}, {mode}:\n"
                + string.Join("\n\n", scans.Select(scan => scan.Plan).Distinct()));
            // The rule's own query was logged, and it looks in every code table.
            var own = plans.Where(plan => plan.Contains("ehr.clinical_codes", StringComparison.Ordinal) && plan.Contains("regexp_matches", StringComparison.Ordinal)).ToList();
            Assert.True(own.Count > 0, $"No plan of the rule's own query was logged {statistics}, {mode}:\n{string.Join("\n\n", plans)}");
            Assert.All(CodeTables, table => Assert.Contains(own, plan => Regex.IsMatch(plan, $@"Index (Only )?Scan using \w+ on {table}\b")));
        }
    }
}
