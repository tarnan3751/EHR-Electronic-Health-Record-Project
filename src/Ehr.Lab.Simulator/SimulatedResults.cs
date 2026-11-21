using System.Buffers.Binary;

namespace Ehr.Lab.Simulator;

// How the simulated lab behaves: an order's results come between MinimumDelay and MaximumDelay after it was signed,
// and a result is outside its range for no reason on the patient's list as often as AbnormalRate says. The defaults
// suit the local stack: results within half a minute, one in twenty out of range by chance.
public sealed record LabSettings(TimeSpan MinimumDelay, TimeSpan MaximumDelay, double AbnormalRate)
{
    public static readonly LabSettings Default = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), 0.05);
}

// One order's results as the simulated lab will post them, and when: collected a quarter of the way into its delay,
// posted once the delay is over.
internal sealed record LabPlan(Guid Order, DateTimeOffset CollectedAt, DateTimeOffset Due, IReadOnlyList<LabResult> Results);

// Synthetic results (the expansion plan's step 12), made the same way every time from the order's ID and the settings,
// so a test can say exactly what an order gets, and two labs, or one started again, post the same. Each value comes
// from the clinic's own range for the test, as the hand-over gives it (the lab keeps no copy of the ranges), to the
// decimals the clinic records: inside the range; outside it in the direction a diagnosis on the patient's list moves
// that test (ConditionEffects); or, for a test nothing moves, outside it now and then (AbnormalRate). A value outside
// is at least one recorded step outside, so rounding never brings it back in, and never below zero.
internal static class SimulatedResults
{
    public static LabPlan For(LabOrder order, LabSettings settings)
    {
        var timing = new SplitMix64(Seed(order.Id, "delay"));
        var delay = settings.MinimumDelay + ((settings.MaximumDelay - settings.MinimumDelay) * timing.NextDouble());
        var results = order.Tests
            // A test with no unit or no range can't be reported here; none in the reference data is like that.
            .Where(test => test.Unit is { Length: > 0 } && (test.Low is not null || test.High is not null))
            .Select(test => new LabResult(test.Code, Value(order.Id, test, order.Conditions, settings), test.Unit!))
            .ToList();
        return new LabPlan(order.Id, order.SignedAt + (delay / 4), order.SignedAt + delay, results);
    }

    static decimal Value(Guid order, LabTest test, IReadOnlyList<string> conditions, LabSettings settings)
    {
        // Three draws for every test, whichever are used, so a change of settings moves nothing else.
        var random = new SplitMix64(Seed(order, test.Code));
        var unusual = random.NextDouble();
        var downward = random.NextDouble();
        var where = (decimal)random.NextDouble();

        var direction = ConditionEffects.Direction(conditions, test.Code);
        if (direction == 0 && unusual < settings.AbnormalRate)
        {
            direction = test.Low is not null && downward < 0.5 ? -1 : 1;
        }

        var decimals = Math.Clamp(test.Decimals, 0, 4);
        var step = 1m / (decimal)Math.Pow(10, decimals);
        // A range with no low, such as LDL's "below 100", is normal from half its high; one with no high, up to twice
        // its low.
        var high = test.High ?? (test.Low!.Value * 2);
        var low = test.Low ?? (high / 2);
        var spread = high - low;
        decimal Round(decimal value) => Math.Round(value, decimals, MidpointRounding.AwayFromZero);

        return direction switch
        {
            > 0 when test.High is not null => Math.Max(Round(high + (spread * (0.05m + (0.6m * where)))), high + step),
            < 0 when test.Low is not null && test.Low.Value - step >= 0 => Math.Max(0m, Math.Min(Round(low - (spread * (0.05m + (0.45m * where)))), low - step)),
            _ => Math.Clamp(Round(low + (spread * where)), low, high),
        };
    }

    // From the order's ID and what is drawn for, so each test's draws are its own whatever else the order holds.
    static ulong Seed(Guid order, string label)
    {
        Span<byte> bytes = stackalloc byte[16];
        order.TryWriteBytes(bytes, bigEndian: true, out _);
        var seed = BinaryPrimitives.ReadUInt64BigEndian(bytes) ^ BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
        foreach (var c in label)
        {
            seed = (seed ^ c) * 0x100000001B3UL;
        }

        return seed;
    }

    // SplitMix64, written out, as the synthetic clinic's is (bench/Ehr.Seed), so an order gets the same results on every
    // .NET version, which promise no particular sequence from a seeded System.Random.
    sealed class SplitMix64(ulong seed)
    {
        ulong state = seed;

        public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

        ulong NextUInt64()
        {
            var z = state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}

// Which diagnoses on a patient's list move which tests, and which way. Only diagnoses and tests this repository has:
// the diagnoses by ICD-10-CM code prefix, every one in the reference data (db/reference/10-clinical-codes.sql), and
// E11, E78, E03 and E55 among the synthetic clinic's (bench/Ehr.Seed); the tests by LOINC code, each one a catalog
// item reports (db/reference/30-observations.sql). The reference data has no eGFR, so kidney disease moves creatinine
// and urea nitrogen. A test nothing here moves is in its range, but for the occasional one outside it.
internal static class ConditionEffects
{
    // What the diagnosis is, its codes' prefixes, and the tests it moves: 1 above the range, -1 below it.
    public static readonly (string Diagnosis, string[] Prefixes, (string Test, int Direction)[] Moves)[] Table =
    [
        ("Diabetes mellitus, type 1 or 2", ["E10", "E11"], [("4548-4", 1), ("2345-7", 1)]),
        ("Disorders of lipoprotein metabolism (hyperlipidemia)", ["E78"], [("2093-3", 1), ("13457-7", 1)]),
        ("Hypothyroidism", ["E03"], [("3016-3", 1)]),
        ("Vitamin D deficiency", ["E55"], [("62292-8", -1)]),
        ("Chronic kidney disease", ["N18"], [("2160-0", 1), ("3094-0", 1)]),
        ("Anemias (D50 to D64)", ["D50", "D51", "D52", "D53", "D55", "D56", "D57", "D58", "D59", "D60", "D61", "D62", "D63", "D64"], [("718-7", -1), ("4544-3", -1)]),
    ];

    // Which way the patient's diagnoses move the test: 1, -1, or 0 for neither.
    public static int Direction(IReadOnlyList<string> conditions, string test) =>
        Table.Where(row => conditions.Any(code => row.Prefixes.Any(prefix => code.StartsWith(prefix, StringComparison.Ordinal))))
            .SelectMany(row => row.Moves)
            .Where(move => move.Test == test)
            .Select(move => move.Direction)
            .FirstOrDefault();
}
