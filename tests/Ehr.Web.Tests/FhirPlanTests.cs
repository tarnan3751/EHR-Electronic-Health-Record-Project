using System.Data.Common;
using System.Text.RegularExpressions;
using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Data.Patients;
using Ehr.Domain.Audit;
using Ehr.Domain.Phi;
using Ehr.Seed;
using Ehr.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace Ehr.Web.Tests;

// The FHIR API's reads (the expansion plan's step 11) keep to their indexes whatever the planner's statistics say. A join
// to the blood pressure diastolics once read every diastolic in the table for each request: on the benchmark's 50,000
// patients a search took a second, and its row didn't finish in CI's half hour. Here, in a database of its own seeded
// with a clinic big enough for a whole-table read to cost something, with autovacuum off, every query the API's reads
// send is EXPLAINed as it is sent, as ehr_app with a clinician in context: first with the statistics cleared, as after
// a bulk load nobody analyzed, then analyzed. None may read the whole of a table the seed filled. The plan is checked
// rather than a time, so the test holds on any machine however busy, and that join showed as a sequential scan at any
// size.
public class FhirPlanTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    const int Patients = 10_000;

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task No_read_scans_a_whole_table_with_the_statistics_cleared_or_gathered()
    {
        var tables = string.Join(", ", Seeder.Tables.Select(table => $"'{table}'"));
        await MigratedDatabase.ExecuteAsync(database.SuperuserConnectionString,
            string.Concat(Seeder.Tables.Select(table => $"ALTER TABLE ehr.{table} SET (autovacuum_enabled = false);")));
        await Seeder.LoadAsync(database.OwnerConnectionString, new SyntheticClinic(Patients), Cancel);
        var clinician = await ClinicianAsync();
        var chart = Enumerable.Range(0, Patients).Select(SyntheticClinic.Chart)
            .First(c => c.Vitals.Any(v => v.ReadingId is not null) && c.Allergies.Count > 0 && c.Medications.Count > 0 && c.Conditions.Count > 0);

        // As never analyzed: PostgreSQL 18 clears a table's statistics and its columns'.
        await MigratedDatabase.ExecuteAsync(database.SuperuserConnectionString,
            $"""
            SELECT pg_clear_relation_stats('ehr', t) FROM unnest(ARRAY[{tables}]) t;
            SELECT pg_clear_attribute_stats('ehr', c.relname, a.attname, false)
            FROM pg_class c JOIN pg_attribute a ON a.attrelid = c.oid
            WHERE c.relnamespace = 'ehr'::regnamespace AND c.relname = ANY (ARRAY[{tables}]) AND a.attnum > 0 AND NOT a.attisdropped;
            """);
        await AssertIndexedAsync(clinician, chart, "with the statistics cleared");

        await MigratedDatabase.ExecuteAsync(database.SuperuserConnectionString,
            $"ANALYZE {string.Join(", ", Seeder.Tables.Select(table => $"ehr.{table}"))}");
        await AssertIndexedAsync(clinician, chart, "analyzed");
    }

    // Every read the API makes, each of its kinds of entry searched and read by ID, as it makes them; and the plan of each
    // query they send.
    async Task AssertIndexedAsync(Guid clinician, SyntheticChart chart, string statistics)
    {
        var user = new UserContext { UserId = clinician };
        var plans = new Plans();
        var options = new DbContextOptionsBuilder<EhrDbContext>();
        EhrDbContext.Configure(options, database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword));
        options.AddInterceptors(new UserContextInterceptor(user), plans);
        await using var db = new EhrDbContext(options.Options);
        var reader = new PhiReader(db, new AuditRecorder(db, user, new AuditSource(AuditSources.OnPrem)), user);
        var patient = chart.Patient.Id;
        var reading = chart.Vitals.First(v => v.ReadingId is not null).ReadingId!.Value;
        var systolic = chart.Vitals.Single(v => v.ReadingId == reading && v.Code == VitalSign.Systolic).Id;
        var plain = chart.Vitals.First(v => v.ReadingId is null).Id;

        await using var transaction = await db.Database.BeginTransactionAsync(Cancel);
        Assert.Equal(patient, await reader.PatientIdByMrnAsync(chart.Patient.Mrn, Cancel));
        Assert.NotNull(await reader.PatientAsync(patient, Cancel));
        Assert.Equal(patient, await reader.EntryPatientAsync("allergy_intolerances", chart.Allergies[0].Id, sensitive: true, Cancel));
        Assert.Equal(patient, await reader.EntryPatientAsync("medication_statements", chart.Medications[0].Id, sensitive: true, Cancel));
        Assert.Equal(patient, await reader.EntryPatientAsync("conditions", chart.Conditions[0].Id, sensitive: true, Cancel));
        Assert.Equal(patient, await reader.EntryPatientAsync("observations", reading, sensitive: false, Cancel));
        Assert.Equal(patient, await reader.EntryPatientAsync("observations", plain, sensitive: false, Cancel));
        Assert.Null(await reader.EntryPatientAsync("observations", systolic, sensitive: false, Cancel));
        Assert.Null(await reader.EntryPatientAsync("appointments", Guid.CreateVersion7(), sensitive: false, Cancel));
        Assert.NotEmpty((await reader.AllergiesAsync(patient, only: null, 600, Cancel)).Entries);
        Assert.Single((await reader.AllergiesAsync(patient, chart.Allergies[0].Id, 1, Cancel)).Entries);
        Assert.NotEmpty((await reader.MedicationsAsync(patient, only: null, sensitive: true, 600, Cancel)).Entries);
        Assert.Single((await reader.MedicationsAsync(patient, chart.Medications[0].Id, sensitive: true, 1, Cancel)).Entries);
        Assert.NotEmpty((await reader.ConditionsAsync(patient, only: null, sensitive: true, 600, Cancel)).Entries);
        Assert.Single((await reader.ConditionsAsync(patient, chart.Conditions[0].Id, sensitive: true, 1, Cancel)).Entries);
        Assert.Contains((await reader.ObservationsAsync(patient, only: null, category: null, 600, Cancel)).Entries, e => e.Diastolic is not null);
        Assert.Contains((await reader.ObservationsAsync(patient, only: null, ObservationCategory.VitalSigns, 600, Cancel)).Entries, e => e.Diastolic is not null);
        // Cut by the limit, so it counts.
        var cut = await reader.ObservationsAsync(patient, only: null, category: null, 1, Cancel);
        Assert.True(cut.Total > cut.Entries.Count);
        Assert.NotNull(Assert.Single((await reader.ObservationsAsync(patient, reading, category: null, 1, Cancel)).Entries).Diastolic);
        Assert.Single((await reader.ObservationsAsync(patient, plain, category: null, 1, Cancel)).Entries);
        Assert.Empty((await reader.ObservationsAsync(patient, systolic, category: null, 1, Cancel)).Entries);
        Assert.Empty((await reader.AllAppointmentsAsync(patient, only: null, 600, Cancel)).Entries);
        await transaction.RollbackAsync(Cancel);

        var scans = plans.Explained
            .SelectMany(plan => Regex.Matches(plan.Plan, @"Seq Scan on (\w+)").Select(scan => (Table: scan.Groups[1].Value, plan.Sql, plan.Plan)))
            .Where(scan => Seeder.Tables.Contains(scan.Table))
            .ToList();
        Assert.True(scans.Count == 0,
            $"Read the whole of {string.Join(", ", scans.Select(scan => scan.Table).Distinct())} {statistics}:\n"
            + string.Join("\n\n", scans.Select(scan => $"{scan.Sql}\n{scan.Plan}")));
    }

    async Task<Guid> ClinicianAsync()
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ehr.staff_users (id, user_name, normalized_user_name, email, normalized_email, email_confirmed,
                phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count, display_name, roles,
                must_change_password)
            VALUES (@id, @email, upper(@email), @email, upper(@email), true, false, false, true, 0, 'Dr. Fhir Planned', @roles, false)
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("email", $"{id:N}@example.com");
        command.Parameters.AddWithValue("roles", new[] { StaffRoles.Clinician });
        await command.ExecuteNonQueryAsync(Cancel);
        return id;
    }

    // The plan of each query as it is sent, with its own parameters, in its transaction, so in the same user's context.
    sealed class Plans : DbCommandInterceptor
    {
        public List<(string Sql, string Plan)> Explained { get; } = [];

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.Ordinal))
            {
                await using var explain = new NpgsqlCommand("EXPLAIN " + command.CommandText, (NpgsqlConnection)command.Connection!, (NpgsqlTransaction?)command.Transaction);
                foreach (NpgsqlParameter parameter in command.Parameters)
                {
                    explain.Parameters.Add(parameter.Clone());
                }

                var lines = new List<string>();
                await using (var plan = await explain.ExecuteReaderAsync(cancellationToken))
                {
                    while (await plan.ReadAsync(cancellationToken))
                    {
                        lines.Add(plan.GetString(0));
                    }
                }

                Explained.Add((command.CommandText, string.Join('\n', lines)));
            }

            return result;
        }
    }
}
