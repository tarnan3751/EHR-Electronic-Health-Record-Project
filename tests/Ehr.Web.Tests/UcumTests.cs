using Ehr.Seed;
using Ehr.Testing;
using Fhir.Metrics;
using Npgsql;
using Xunit;

namespace Ehr.Web.Tests;

// Every unit the reference data and the synthetic seed record is UCUM (Tahseen's decision, 2026-11-19, after the seed's
// thyrotropin was found in "mIU/L"), and every seeded result and vital sign is in its code's own unit: so the FHIR API
// (the expansion plan's step 11) can send each with UCUM's system and code.
//
// Checked with Fhir.Metrics, Firely's UCUM implementation: it reads UCUM's own table of units and prefixes and parses a
// unit by UCUM's grammar, annotations such as {score} included, refusing anything it can't resolve. It says whether a
// unit is UCUM, not whether it is the right one for its test; that the seed uses its code's reference unit covers that.
public class UcumTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    static readonly SystemOfUnits Ucum = UCUM.Load();

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    static bool IsUcum(string unit)
    {
        try
        {
            Ucum.Metric(unit);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public void The_check_tells_ucum_from_the_ways_units_are_usually_written()
    {
        Assert.All(new[] { "m[IU]/L", "mm[Hg]", "10*3/uL", "/min", "Cel", "kg/m2", "{score}", "%" }, unit => Assert.True(IsUcum(unit), unit));
        Assert.All(new[] { "mIU/L", "mmHg", "bpm", "x10E3/uL", "degC", "mcg" }, unit => Assert.False(IsUcum(unit), unit));
    }

    [Fact]
    public async Task Every_unit_in_the_reference_data_is_ucum()
    {
        var units = await UnitsAsync();

        Assert.True(units.Count > 20, $"Only {units.Count} codes with a unit.");
        Assert.All(units, code => Assert.True(IsUcum(code.Value), $"{code.Key}: '{code.Value}' isn't UCUM."));
    }

    [Fact]
    public async Task Every_unit_the_seed_records_is_its_codes_own_ucum_unit()
    {
        var units = await UnitsAsync();
        var seeded = Enumerable.Range(0, 3000).Select(SyntheticClinic.Chart).SelectMany(chart => chart.Results.Concat(chart.Vitals))
            .Select(observation => (Code: observation.Code!, Unit: observation.Unit!)).Distinct().ToList();

        Assert.Contains(seeded, observation => observation.Code == "3016-3");
        Assert.All(seeded, observation =>
        {
            Assert.True(units.TryGetValue(observation.Code, out var unit), $"{observation.Code} isn't in the reference data.");
            Assert.True(observation.Unit == unit, $"{observation.Code}: the seed records '{observation.Unit}', the reference data '{unit}'.");
            Assert.True(IsUcum(observation.Unit), $"{observation.Code}: '{observation.Unit}' isn't UCUM.");
        });
    }

    // Each code's unit, from ehr.observation_codes as db/reference loads it.
    async Task<Dictionary<string, string>> UnitsAsync()
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand("SELECT code, unit FROM ehr.observation_codes WHERE unit IS NOT NULL", connection);
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        var units = new Dictionary<string, string>();
        while (await reader.ReadAsync(Cancel))
        {
            units[reader.GetString(0)] = reader.GetString(1);
        }

        return units;
    }
}
