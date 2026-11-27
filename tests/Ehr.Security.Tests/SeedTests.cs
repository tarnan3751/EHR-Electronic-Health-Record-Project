using System.Text.RegularExpressions;
using Ehr.Seed;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// The synthetic seed (bench/Ehr.Seed), run as ehr_owner the way it runs against the stack's primary. To load, it lifts
// forced row-level security and the write-audit trigger on each table it fills, inside its one transaction: so it must
// leave both as they were, fill only an empty clinic, and on any failure leave nothing behind. Each class here has a
// database of its own, since a seed fills it.
public partial class SeedTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    [Fact]
    public async Task A_seed_fills_an_empty_clinic_once_and_leaves_it_protected()
    {
        var loaded = await Seeder.LoadAsync(database.OwnerConnectionString, new SyntheticClinic(200), TestContext.Current.CancellationToken);

        var rows = await Seeded.RowsAsync(database);
        Assert.Equal(200, rows["patients"]);
        Assert.All(Seeder.Tables, table => Assert.True(rows[table] > 0, $"{table} is empty"));
        Assert.Equal(Seeder.Tables.ToDictionary(table => table, table => (long)loaded[table]), rows);
        await Seeded.AssertProtectedAsync(database);
        // Synthetic rows in an empty clinic, loaded with the trigger off: no write-audit events.
        Assert.Equal(0, await Seeded.WriteEventsAsync(database));
        // Each visit's blood pressure is one reading (step 9c). The seed loads them with the database's check of readings
        // off, as it does the audit trigger, so every reading is checked here as the database would: one patient, time and
        // visit, and exactly one standing systolic and one standing diastolic, nothing else.
        Assert.Equal(["true 0"], await Accounts.QueryAsync(database.SuperuserConnectionString, null,
            """
            SELECT (count(*) > 0) || ' ' || count(*) FILTER (WHERE NOT whole)
            FROM (
                SELECT reading_id IS NOT NULL AND count(*) = 2 AND count(DISTINCT patient_id) = 1 AND count(DISTINCT effective_at) = 1
                    AND count(DISTINCT encounter_id) + max(CASE WHEN encounter_id IS NULL THEN 1 ELSE 0 END) = 1
                    AND count(*) FILTER (WHERE code = '8480-6' AND status <> 'entered-in-error') = 1
                    AND count(*) FILTER (WHERE code = '8462-4' AND status <> 'entered-in-error') = 1 AS whole
                FROM ehr.observations WHERE code IN ('8480-6', '8462-4') GROUP BY reading_id) r
            """));

        // Once there are patients, the seed refuses, and nothing changes.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Seeder.LoadAsync(database.OwnerConnectionString, new SyntheticClinic(10), TestContext.Current.CancellationToken));
        Assert.Contains("already has patients", refused.Message);
        Assert.Equal(rows, await Seeded.RowsAsync(database));
        await Seeded.AssertProtectedAsync(database);
    }

    [Fact]
    public void The_same_clinic_comes_out_every_time()
    {
        var first = SyntheticClinic.Chart(1234);
        var again = SyntheticClinic.Chart(1234);

        Assert.Equal(first.Patient.Id, again.Patient.Id);
        Assert.Equal((first.Patient.GivenName, first.Patient.FamilyName, first.Patient.BirthDate), (again.Patient.GivenName, again.Patient.FamilyName, again.Patient.BirthDate));
        Assert.Equal(first.Visits.Select(v => (v.Id, v.PeriodStart)), again.Visits.Select(v => (v.Id, v.PeriodStart)));
        // The vital signs taken at the visits are observations (step 9): the same ones, with the same values.
        Assert.NotEmpty(first.Vitals);
        Assert.Equal(first.Vitals.Select(v => (v.Id, v.EncounterId, v.ReadingId, v.Code, v.Value)), again.Vitals.Select(v => (v.Id, v.EncounterId, v.ReadingId, v.Code, v.Value)));
        Assert.Equal("SYN0001235", first.Patient.Mrn);

        // Steps 9b and 9d: every seeded vital sign carries the range for the patient's age on the day of the visit and
        // their sex, and the seeded clinic has children of every band among them. Healthy synthetic children are mostly
        // unflagged: of each sign, at most one child's value in seven is flagged (a blood pressure at or above its
        // screening value, as the pages flag it).
        var seen = new Dictionary<string, (int In, int Out)>();
        var children = new Dictionary<string, (int All, int Flagged)>();
        foreach (var chart in Enumerable.Range(0, 3000).Select(SyntheticClinic.Chart))
        {
            foreach (var vital in chart.Vitals)
            {
                var months = Ehr.Data.Reference.AgeBand.Months(chart.Patient.BirthDate, DateOnly.FromDateTime(vital.EffectiveAt.UtcDateTime));
                var range = Ehr.Data.Reference.VitalRanges.For(vital.Code!, months, chart.Patient.Gender);
                Assert.Equal((range?.Low, range?.High, range?.Kind ?? Ehr.Data.Patients.ReferenceKind.Normal), (vital.ReferenceLow, vital.ReferenceHigh, vital.ReferenceKind));
                var band = Ehr.Data.Reference.AgeBand.Of(months);
                var outside = vital.ReferenceKind == Ehr.Data.Patients.ReferenceKind.Screening
                    ? vital.Value >= vital.ReferenceHigh
                    : vital.Value < vital.ReferenceLow || vital.Value > vital.ReferenceHigh;
                var (inside, flagged) = seen.GetValueOrDefault(band);
                seen[band] = outside ? (inside, flagged + 1) : (inside + 1, flagged);
                if (band != Ehr.Data.Reference.AgeBand.Adult)
                {
                    var (all, flaggedChildren) = children.GetValueOrDefault(vital.Code!);
                    children[vital.Code!] = (all + 1, flaggedChildren + (outside ? 1 : 0));
                }
            }
        }

        Assert.Equal(Ehr.Data.Reference.AgeBand.All.Order(), seen.Keys.Order());
        Assert.All(seen.Values, counts => Assert.True(counts.In > counts.Out, $"More seeded values outside the range than inside: {counts}"));
        Assert.All(children, sign => Assert.True(sign.Value.Flagged * 7 <= sign.Value.All, $"Too many seeded children flagged for {sign.Key}: {sign.Value}"));
        Assert.Equal(7, first.Patient.Id.Version);
    }

    // The clinic's names are ordinary ones, with no number in them: each from the seed's lists, the given name one for the
    // patient's gender, every family name long enough for the benchmark's four-letter search. Across a clinic of 50,000
    // a full name repeats now and then, as in a real clinic, and never often.
    [Fact]
    public void The_clinics_names_are_plain_names_from_its_lists()
    {
        var patients = Enumerable.Range(0, 50_000).Select(i => SyntheticClinic.Chart(i).Patient).ToList();

        Assert.DoesNotContain(patients, p => !PlainName().IsMatch(p.GivenName) || !PlainName().IsMatch(p.FamilyName));
        Assert.DoesNotContain(patients, p => !SyntheticClinic.GivenNames(p.Gender).Contains(p.GivenName) || !SyntheticClinic.FamilyNames.Contains(p.FamilyName));
        Assert.All(SyntheticClinic.FamilyNames, name => Assert.True(name.Length >= 4, name));
        Assert.InRange(patients.CountBy(p => (p.GivenName, p.FamilyName)).Max(name => name.Value), 2, 10);
    }

    [GeneratedRegex("^[A-Z][a-z]+$")]
    private static partial Regex PlainName();
}

public class SeedRollbackTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    [Fact]
    public async Task A_seed_that_fails_part_way_leaves_the_clinic_as_it_was()
    {
        // A constraint every new visit breaks fails the seed after the four tables before visits have loaded.
        await MigratedDatabase.ExecuteAsync(database.SuperuserConnectionString,
            "ALTER TABLE ehr.encounters ADD CONSTRAINT ck_seed_test_no_visits CHECK (false) NOT VALID");
        try
        {
            var failed = await Assert.ThrowsAsync<PostgresException>(
                () => Seeder.LoadAsync(database.OwnerConnectionString, new SyntheticClinic(50), TestContext.Current.CancellationToken));
            Assert.Equal(PostgresErrorCodes.CheckViolation, failed.SqlState);
        }
        finally
        {
            await MigratedDatabase.ExecuteAsync(database.SuperuserConnectionString,
                "ALTER TABLE ehr.encounters DROP CONSTRAINT ck_seed_test_no_visits");
        }

        Assert.All(await Seeded.RowsAsync(database), table => Assert.Equal(0, table.Value));
        await Seeded.AssertProtectedAsync(database);
    }
}

// The expansion plan's step 2a, in a clinic of its own: the seed fills only an empty one.
public class SeedCoherenceTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    // Step 2a: the clinic is coded and coherent. Every code a seeded chart carries is in the reference data, under the
    // same name; each medication treats a condition the patient has, and none contains something they are allergic to;
    // a diagnosis in a sensitive range is marked by the database, and so is the medication that treats it; and a
    // condition moves the results it is followed with, such as hemoglobin A1c with diabetes.
    [Fact]
    public async Task The_seeded_clinic_is_coded_from_the_reference_data_and_clinically_coherent()
    {
        var clinic = new SyntheticClinic(400);
        await Seeder.LoadAsync(database.OwnerConnectionString, clinic, TestContext.Current.CancellationToken);

        foreach (var (table, kind, system) in new[]
        {
            ("conditions", "condition", "'icd-10-cm'"), ("medication_statements", "medication", "'rxnorm'"), ("allergy_intolerances", "allergen", "t.code_system"),
        })
        {
            Assert.Equal(0L, await Seeded.ScalarAsync<long>(database,
                $"""
                SELECT count(*) FROM ehr.{table} t
                WHERE t.code IS DISTINCT FROM 'no-known-allergy' AND NOT EXISTS (
                    SELECT 1 FROM ehr.clinical_codes c WHERE c.kind = '{kind}' AND c.system = {system} AND c.code = t.code AND c.display = t.display)
                """));
        }

        Assert.Equal(0L, await Seeded.ScalarAsync<long>(database,
            "SELECT count(*) FROM ehr.observations o WHERE NOT EXISTS (SELECT 1 FROM ehr.observation_codes c WHERE c.code = o.code)"));
        // No medication with an ingredient the patient has an active allergy to.
        Assert.Equal(0L, await Seeded.ScalarAsync<long>(database,
            """
            SELECT count(*) FROM ehr.medication_statements m
            JOIN ehr.drug_ingredients i ON i.product_code = m.code
            JOIN ehr.allergy_intolerances a ON a.patient_id = m.patient_id AND a.code_system = 'rxnorm' AND a.code = i.ingredient_code AND a.clinical_status = 'active'
            """));
        // Sensitive by the code's range, set by the database; and the medication that treats it, set by the seed.
        Assert.Equal(0L, await Seeded.ScalarAsync<long>(database, "SELECT count(*) FROM ehr.conditions WHERE sensitive <> ehr.icd10cm_is_sensitive(code)"));
        Assert.True(await Seeded.ScalarAsync<long>(database, "SELECT count(*) FROM ehr.conditions WHERE sensitive") > 20);
        Assert.Equal(0L, await Seeded.ScalarAsync<long>(database,
            """
            SELECT count(*) FROM ehr.medication_statements m
            WHERE m.sensitive <> EXISTS (SELECT 1 FROM ehr.conditions c WHERE c.patient_id = m.patient_id AND c.sensitive)
              AND m.code IN ('312941', '349332', '351267')
            """));
        // Diabetes raises hemoglobin A1c above the range; without it, most results are inside.
        Assert.True(await Seeded.ScalarAsync<bool>(database,
            """
            SELECT bool_and(o.value > o.reference_high) FROM ehr.observations o
            WHERE o.code = '4548-4' AND EXISTS (SELECT 1 FROM ehr.conditions c WHERE c.patient_id = o.patient_id AND c.code = 'E11.9')
            """));
        Assert.True(await Seeded.ScalarAsync<long>(database,
            "SELECT count(*) FROM ehr.medication_statements m JOIN ehr.conditions c ON c.patient_id = m.patient_id AND c.code = 'E11.9' WHERE m.display LIKE 'metformin%'") > 5);
        // Still protected, with no history and no write-audit rows for seeded entries.
        await Seeded.AssertProtectedAsync(database);
        Assert.Equal(0L, await Seeded.ScalarAsync<long>(database, "SELECT count(*) FROM ehr.chart_history"));
    }
}

// What the seed left behind, read as the superuser.
static class Seeded
{
    public static async Task<Dictionary<string, long>> RowsAsync(MigratedDatabase database)
    {
        var rows = new Dictionary<string, long>();
        foreach (var table in Seeder.Tables)
        {
            rows[table] = await ScalarAsync<long>(database, $"SELECT count(*) FROM ehr.{table}");
        }

        return rows;
    }

    public static Task<long> WriteEventsAsync(MigratedDatabase database) =>
        ScalarAsync<long>(database, $"SELECT count(*) FROM ehr.audit_events WHERE table_name IN ({string.Join(", ", Seeder.Tables.Select(t => $"'{t}'"))})");

    // Row-level security enabled and forced, and the write-audit trigger enabled, on every table the seed fills.
    public static async Task AssertProtectedAsync(MigratedDatabase database)
    {
        foreach (var table in Seeder.Tables)
        {
            Assert.True(await ScalarAsync<bool>(database,
                $"SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid = 'ehr.{table}'::regclass"),
                $"{table}: row-level security isn't forced");
            Assert.Equal("O", await ScalarAsync<string>(database,
                $"SELECT tgenabled::text FROM pg_trigger WHERE tgrelid = 'ehr.{table}'::regclass AND tgname = '{table}_audit'"));
            // Nor any other trigger of the table's own left off, such as a chart list's history or guard trigger.
            Assert.Equal(0L, await ScalarAsync<long>(database,
                $"SELECT count(*) FROM pg_trigger WHERE tgrelid = 'ehr.{table}'::regclass AND NOT tgisinternal AND tgenabled <> 'O'"));
        }
    }

    public static async Task<T> ScalarAsync<T>(MigratedDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
