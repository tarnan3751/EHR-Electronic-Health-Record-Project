using System.Text.RegularExpressions;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Migrations;
using Ehr.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// The expansion plan's step 13c2: which part of the record a read was for (ehr.audit_events.part, AuditParts). The
// database takes one of the list, on a read, and nothing else; and the migration that added it never held the audit
// log for long, on a log already in use: no rewrite, no read of the table for the check, and a lock it can't have soon
// is given up rather than waited for. A database of its own: it migrates down and up.
public partial class AuditPartSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    const string Migration = "AuditEventParts";

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    Task<List<string>> AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.QueryAsync(database.SuperuserConnectionString, null, sql, parameters);

    // As the migration job does it (EhrDbContextFactory): to the migration before this step's, or to the latest.
    async Task MigrateAsync(bool before)
    {
        await using var context = EhrDbContextFactory.Create(database.OwnerConnectionString, TextWriter.Null);
        var all = context.Database.GetMigrations().ToList();
        var target = before ? all[all.FindIndex(name => name.EndsWith("_" + Migration, StringComparison.Ordinal)) - 1] : null;
        await context.GetService<IMigrator>().MigrateAsync(target, Cancel);
    }

    Task<List<string>> ColumnAsync() =>
        AsSuperuserAsync("SELECT column_name::text FROM information_schema.columns WHERE table_schema = 'ehr' AND table_name = 'audit_events' AND column_name = 'part'");

    [GeneratedRegex(@"'([^']*)'::character varying")]
    private static partial Regex Quoted();

    [Fact]
    public async Task The_log_takes_a_part_only_on_a_read_and_only_one_of_the_list()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        Task InsertAsync(string[] actions, string?[] parts) => Accounts.ExecuteAsync(App, clinician,
            """
            INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source, part)
            SELECT uuidv7(), now(), @actor, a.action, 'patients', @patient, @patient, 'onprem', p.part
            FROM unnest(@actions) AS a(action) CROSS JOIN unnest(@parts::text[]) AS p(part)
            """, ("actor", clinician), ("patient", patient), ("actions", actions), ("parts", parts));

        // Each part on each kind of read, and none on any event.
        await InsertAsync(AuditActions.Reads, AuditParts.All);
        await InsertAsync([AuditActions.Read, AuditActions.SignIn, AuditActions.Insert], [null]);
        Assert.Equal([(AuditActions.Reads.Length * AuditParts.All.Length).ToString(System.Globalization.CultureInfo.InvariantCulture)],
            await AsSuperuserAsync("SELECT count(*) FROM ehr.audit_events WHERE patient_id = @patient AND part IS NOT NULL", ("patient", patient)));

        // Nothing else: not another spelling, an address with an ID, or a part on an event that isn't a read.
        foreach (var (action, part) in new[]
        {
            (AuditActions.Read, "Chart"), (AuditActions.Read, "chart "), (AuditActions.Read, ""), (AuditActions.Read, $"/patients/{patient}"[..24]),
            (AuditActions.Read, "notes"), (AuditActions.SignIn, AuditParts.Chart), (AuditActions.Insert, AuditParts.Chart),
            (AuditActions.SentToLab, AuditParts.Orders), (AuditActions.BreakGlass, AuditParts.Chart),
        })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync([action], [part]));
            Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
        }

        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation,
            (await Assert.ThrowsAsync<PostgresException>(() => InsertAsync([AuditActions.Read], [new string('a', 25)]))).SqlState);

        // The database's lists are the code's: the reads, and the parts, no more and no fewer.
        var check = Assert.Single(await AsSuperuserAsync("SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ck_audit_events_part'"));
        var at = check.IndexOf("(part)::text", StringComparison.Ordinal);
        Assert.Equal(AuditActions.Reads.Order(), Quoted().Matches(check[..at]).Select(match => match.Groups[1].Value).Order());
        Assert.Equal(AuditParts.All.Order(), Quoted().Matches(check[at..]).Select(match => match.Groups[1].Value).Order());
    }

    // On a log already holding events: the column changes the catalog alone (the table's file is the same one), each
    // event already there has no part, and the check was added without reading them (not validated). The index is there.
    [Fact]
    public async Task The_migration_adds_the_part_without_rewriting_or_reading_the_log()
    {
        await MigrateAsync(before: true);
        try
        {
            await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
                """
                INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source)
                SELECT uuidv7(), now() - make_interval(mins => n), gen_random_uuid(), 'read', 'patients', NULL, gen_random_uuid(), 'onprem'
                FROM generate_series(1, 2000) AS n
                """);
            const string File = "SELECT pg_relation_filenode('ehr.audit_events')::text";
            var file = await AsSuperuserAsync(File);
            Assert.Empty(await ColumnAsync());

            await MigrateAsync(before: false);
            Assert.Equal(file, await AsSuperuserAsync(File));
            Assert.Equal(["0"], await AsSuperuserAsync("SELECT count(*)::text FROM ehr.audit_events WHERE part IS NOT NULL"));
            Assert.Equal(["false"], await AsSuperuserAsync("SELECT convalidated::text FROM pg_constraint WHERE conname = 'ck_audit_events_part'"));
            Assert.Equal(["CREATE INDEX ix_patient_assignments_history ON ehr.patient_assignments USING btree (patient_id, clinician_id)"],
                await AsSuperuserAsync("SELECT indexdef FROM pg_indexes WHERE schemaname = 'ehr' AND indexname = 'ix_patient_assignments_history'"));
        }
        finally
        {
            await MigrateAsync(before: false);
        }
    }

    // While another session holds the log, even only to read it, the migration fails with the lock refused, and leaves
    // nothing done, rather than wait with every insert of the app queued behind it; the job's next run does it. The lock
    // is held from another connection for the whole attempt, so the outcome doesn't depend on how long anything takes.
    [Fact]
    public async Task The_migration_gives_up_on_a_log_another_session_holds_rather_than_queue_behind_it()
    {
        await MigrateAsync(before: true);
        try
        {
            await using (var holder = new NpgsqlConnection(database.SuperuserConnectionString))
            {
                await holder.OpenAsync(Cancel);
                await using var transaction = await holder.BeginTransactionAsync(Cancel);
                await using (var hold = new NpgsqlCommand("LOCK TABLE ehr.audit_events IN ACCESS SHARE MODE", holder, transaction))
                {
                    await hold.ExecuteNonQueryAsync(Cancel);
                }

                var failure = await Assert.ThrowsAnyAsync<Exception>(() => MigrateAsync(before: false));
                var refused = Assert.IsType<PostgresException>(failure is PostgresException ? failure : failure.InnerException);
                Assert.Equal(PostgresErrorCodes.LockNotAvailable, refused.SqlState);
                await transaction.RollbackAsync(Cancel);
            }

            Assert.Empty(await ColumnAsync());
            Assert.Empty(await AsSuperuserAsync("SELECT indexname::text FROM pg_indexes WHERE indexname = 'ix_patient_assignments_history'"));
            Assert.Empty(await AsSuperuserAsync($"SELECT migration_id FROM ehr.__ef_migrations_history WHERE migration_id LIKE '%\\_{Migration}'"));
        }
        finally
        {
            await MigrateAsync(before: false);
        }

        Assert.Equal(["part"], await ColumnAsync());
    }
}
