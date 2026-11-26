using System.Globalization;
using Ehr.Lab.Simulator;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Web.Tests;

// The simulated lab's values (the expansion plan's step 12, src/Ehr.Lab.Simulator): the same for the same order and
// settings, from the clinic's own ranges as the hand-over gives them (read here from the reference data), moved by the
// diagnoses its table names and by nothing else but the abnormal rate, and always what the clinic records: never below
// zero, to the range's decimals, and outside the range by at least one of them when outside at all.
public class LabSimulatorTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    static readonly DateTimeOffset Signed = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);

    static readonly LabTest HemoglobinA1c = new("4548-4", "%", 4.0m, 5.6m, 1);

    static readonly LabTest Glucose = new("2345-7", "mg/dL", 70m, 99m, 0);

    static LabOrder Order(Guid id, string[] conditions, params LabTest[] tests) => new(id, Signed, tests, conditions);

    // Every test a lab order reports, as the hand-over gives it (db/policies/76-lab.sql).
    async Task<List<LabTest>> ReportedTestsAsync()
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand(
            """
            SELECT DISTINCT c.code, c.unit, c.reference_low, c.reference_high,
                   greatest(coalesce(scale(c.reference_low), 0), coalesce(scale(c.reference_high), 0))
            FROM ehr.order_result_codes r JOIN ehr.observation_codes c ON c.code = r.observation_code
            ORDER BY c.code
            """, connection);
        var tests = new List<LabTest>();
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        while (await reader.ReadAsync(Cancel))
        {
            tests.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                reader.IsDBNull(3) ? null : reader.GetDecimal(3), reader.GetInt32(4)));
        }

        return tests;
    }

    static int Decimals(decimal value) => (decimal.GetBits(value)[3] >> 16) & 0xFF;

    [Fact]
    public void The_same_order_and_settings_give_the_same_results_and_delay()
    {
        var id = Guid.Parse("0193f2a4-6c1e-7b3a-9d42-5e0f8a1b2c3d");

        var diabetic = SimulatedResults.For(Order(id, ["E11.9", "I10"], HemoglobinA1c, Glucose), LabSettings.Default);
        var other = SimulatedResults.For(Order(id, [], HemoglobinA1c, Glucose), LabSettings.Default);

        Assert.Equal(diabetic, SimulatedResults.For(Order(id, ["E11.9", "I10"], HemoglobinA1c, Glucose), LabSettings.Default), new PlanComparer());
        // With type 2 diabetes, both above their ranges (4.0 to 5.6 %, 70 to 99 mg/dL); without it, both inside.
        Assert.Equal(["4548-4 6.6 %", "2345-7 103 mg/dL"], diabetic.Results.Select(r => $"{r.Code} {r.Value.ToString(CultureInfo.InvariantCulture)} {r.Unit}"));
        Assert.Equal(["4548-4 5.5 %", "2345-7 74 mg/dL"], other.Results.Select(r => $"{r.Code} {r.Value.ToString(CultureInfo.InvariantCulture)} {r.Unit}"));
        // Due 5.6941166 seconds after signing, collected a quarter of the way in, whatever the diagnoses.
        Assert.Equal(Signed.AddTicks(56_941_166), diabetic.Due);
        Assert.Equal(Signed.AddTicks(14_235_292), diabetic.CollectedAt);
        Assert.Equal((diabetic.Due, diabetic.CollectedAt), (other.Due, other.CollectedAt));
    }

    [Fact]
    public async Task Every_value_is_what_the_clinic_records_and_is_outside_its_range_exactly_when_it_should_be()
    {
        var tests = await ReportedTestsAsync();
        Assert.Equal(21, tests.Count);
        var conditions = ConditionEffects.Table.SelectMany(row => row.Prefixes.Take(1)).Select(prefix => prefix + ".9").ToArray();
        foreach (var test in tests)
        {
            for (var i = 0; i < 300; i++)
            {
                var id = Guid.CreateVersion7();
                var moved = ConditionEffects.Direction(conditions, test.Code);
                foreach (var (list, rate) in new[] { (conditions, 0.0), (Array.Empty<string>(), 0.0), (Array.Empty<string>(), 1.0) })
                {
                    var value = Assert.Single(SimulatedResults.For(Order(id, list, test), new LabSettings(TimeSpan.Zero, TimeSpan.Zero, rate)).Results).Value;
                    var step = 1m / (decimal)Math.Pow(10, test.Decimals);
                    Assert.True(value >= 0 && value < 100_000_000 && Decimals(value) <= test.Decimals, $"{test.Code}: {value}");
                    var above = test.High is { } top && value >= top + step;
                    var below = test.Low is { } bottom && value <= bottom - step;
                    var inside = (test.Low is null || value >= test.Low) && (test.High is null || value <= test.High);
                    var direction = list.Length > 0 ? moved : 0;
                    Assert.True(
                        direction > 0 ? above : direction < 0 ? below : rate == 0.0 ? inside : above || below,
                        $"{test.Code} ({test.Low} to {test.High}), moved {direction}, abnormal rate {rate}: {value}");
                }
            }
        }
    }

    [Fact]
    public void Each_diagnosis_in_the_table_moves_its_tests_the_way_it_says()
    {
        foreach (var (_, prefixes, moves) in ConditionEffects.Table)
        {
            foreach (var prefix in prefixes)
            {
                foreach (var (test, direction) in moves)
                {
                    Assert.Equal(direction, ConditionEffects.Direction([prefix + ".9"], test));
                    Assert.Equal(0, ConditionEffects.Direction(["J45.909"], test));
                }
            }
        }

        // A code only shares a prefix when it starts with it: E1 is no diabetes.
        Assert.Equal(0, ConditionEffects.Direction(["E1"], "4548-4"));
    }

    [Fact]
    public void About_the_abnormal_rate_of_results_are_out_of_range_when_nothing_moves_them()
    {
        var outside = Enumerable.Range(0, 10_000)
            .Select(_ => SimulatedResults.For(Order(Guid.CreateVersion7(), [], HemoglobinA1c), LabSettings.Default).Results[0].Value)
            .Count(value => value is < 4.0m or > 5.6m);

        Assert.InRange(outside, 400, 600);
    }

    [Fact]
    public void Results_come_between_the_delays_collected_a_quarter_of_the_way_in()
    {
        var settings = new LabSettings(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(40), 0.05);
        foreach (var plan in Enumerable.Range(0, 1_000).Select(_ => SimulatedResults.For(Order(Guid.CreateVersion7(), [], Glucose), settings)))
        {
            Assert.InRange(plan.Due - Signed, settings.MinimumDelay, settings.MaximumDelay);
            Assert.Equal(Signed + ((plan.Due - Signed) / 4), plan.CollectedAt);
        }
    }

    sealed class PlanComparer : IEqualityComparer<LabPlan>
    {
        public bool Equals(LabPlan? x, LabPlan? y) =>
            x is not null && y is not null && x.Order == y.Order && x.CollectedAt == y.CollectedAt && x.Due == y.Due && x.Results.SequenceEqual(y.Results);

        public int GetHashCode(LabPlan plan) => plan.Order.GetHashCode();
    }
}
