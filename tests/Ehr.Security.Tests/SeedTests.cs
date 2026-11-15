using Ehr.Seed;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// The synthetic seed (bench/Ehr.Seed), run as ehr_owner the way it runs against the stack's primary. To load, it lifts
// forced row-level security and the write-audit trigger on each table it fills, inside its one transaction: so it must
// leave both as they were, fill only an empty clinic, and on any failure leave nothing behind. Each class here has a
// database of its own, since a seed fills it.
public class SeedTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
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
        Assert.Equal(first.Visits.Select(v => (v.Id, v.PeriodStart, v.SystolicMmHg)), again.Visits.Select(v => (v.Id, v.PeriodStart, v.SystolicMmHg)));
        Assert.Equal("SYN0001235", first.Patient.Mrn);
        Assert.Equal(7, first.Patient.Id.Version);
    }
}

public class SeedRollbackTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    [Fact]
    public async Task A_seed_that_fails_part_way_leaves_the_clinic_as_it_was()
    {
        // Visits load last. A constraint every new visit breaks fails the seed after the other four tables have loaded.
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
        }
    }

    static async Task<T> ScalarAsync<T>(MigratedDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
