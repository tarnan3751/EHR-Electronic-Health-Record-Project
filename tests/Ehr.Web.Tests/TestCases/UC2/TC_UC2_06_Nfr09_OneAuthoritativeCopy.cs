using System.Net;
using Ehr.Testing;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OffSiteOutageTests;

namespace Ehr.Web.Tests.TestCases.UC2;

// Test Case ID: TC-UC2-06
// Purpose / scenario: There is one authoritative copy of the record, the on-site primary. The cloud's read role can
//   write no table; a write refused during an outage leaves the primary's rows unchanged; and after recovery the
//   save lands once, on the primary.
// Type: Non-functional (data integrity: one authoritative copy)
// Requirements: NFR-09 (FR-36 as supporting evidence)
// Use case: UC 2, Read records during an on-site outage
// Preconditions: The database is migrated with db/policies applied. The clinician signed in off-site while the
//   on-site server was up. For the outage, the on-site app's database is down (PrimaryDown on the CloudPrimaryDrill
//   app) and the cloud app's check has found that out.
// Inputs and system conditions: The database's own record of privileges, and statements run as ehr_read, the role
//   the cloud app reads the replica with. On the cloud app: POST /quick-texts?handler=Add as htmx sends it, during
//   the outage and after recovery.
// Test data: A quick text with a new random shortcut and the body "Synthetic: one copy". As ehr_read:
//   UPDATE ehr.quick_texts SET body = body, and DELETE FROM ehr.patients.
// Steps:
//   1. Ask the database which tables of schema ehr the role ehr_read may INSERT into, UPDATE, DELETE from or TRUNCATE.
//   2. As ehr_read, run the UPDATE and the DELETE.
//   3. During the outage, add the quick text off-site, and read the primary's rows for its shortcut.
//   4. Bring the on-site database back, let the cloud app's check run, and add the quick text again.
//   5. Read the primary's rows for its shortcut.
// Expected result: Step 1 lists no table. Step 2's statements are both refused, SQLSTATE 42501 (insufficient
//   privilege). Step 3's post is answered 500 by the on-site app, which can't save without its database, and the
//   primary has no row for the shortcut. Step 4's post is answered
//   200, and the primary then has exactly one row for the shortcut, at version 1. Acceptance criterion: zero tables
//   writable by ehr_read, zero rows written by a refused write, exactly one row after recovery. Supporting evidence:
//   the point-in-time restore drill recorded in docs/UC2.md ("Restore drill"), where the restored copy's row counts
//   matched the primary's at the target time.
// Creator: Tahseen Arnan
public class TC_UC2_06_Nfr09_OneAuthoritativeCopy(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task The_clouds_read_role_can_write_no_table()
    {
        await using (var catalog = await apps.OpenDatabaseAsync())
        {
            await using var writable = new NpgsqlCommand(
                """
                SELECT coalesce(string_agg(c.relname, ', ' ORDER BY c.relname), '')
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'ehr' AND c.relkind IN ('r', 'p')
                  AND (has_table_privilege('ehr_read', c.oid, 'INSERT') OR has_table_privilege('ehr_read', c.oid, 'UPDATE')
                    OR has_table_privilege('ehr_read', c.oid, 'DELETE') OR has_table_privilege('ehr_read', c.oid, 'TRUNCATE'))
                """, catalog);
            Assert.Equal("", (string?)await writable.ExecuteScalarAsync(Cancel));
        }

        await using var asReadRole = new NpgsqlConnection(apps.Database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword));
        await asReadRole.OpenAsync(Cancel);
        foreach (var statement in new[] { "UPDATE ehr.quick_texts SET body = body", "DELETE FROM ehr.patients" })
        {
            await using var write = new NpgsqlCommand(statement, asReadRole);
            var refused = await Assert.ThrowsAsync<PostgresException>(() => write.ExecuteNonQueryAsync(Cancel));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        }
    }

    [Fact]
    public async Task A_write_refused_during_an_outage_changes_nothing_and_after_recovery_the_save_lands_once_on_the_primary()
    {
        var session = await SignInAsync(apps.CloudPrimaryDrill, await apps.ClinicianAsync(), via: apps.Cloud);
        var shipper = apps.CloudPrimaryDrill.Services.GetRequiredService<CloudAuditShipper>();
        var shortcut = WebApps.NewShortcut();
        // Loaded while the clinic's server could be reached; sent once it can't.
        var token = Token(await session.Client.GetStringAsync(QuickTexts, Cancel));
        try
        {
            apps.PrimaryDown = true;
            Assert.False(await shipper.ShipAsync(Cancel));
            using (var refused = await HtmxPostAsync(session.Client, $"{QuickTexts}?handler=Add", token, ("Shortcut", shortcut), ("Body", "Synthetic: one copy")))
            {
                // The on-site app answers, but can't save without its database.
                Assert.Equal(HttpStatusCode.InternalServerError, refused.StatusCode);
            }

            apps.PrimaryDown = false;
            Assert.Empty(await apps.RowsAsync(shortcut));

            Assert.True(await shipper.ShipAsync(Cancel));
            var page = await session.Client.GetStringAsync(QuickTexts, Cancel);
            using var saved = await HtmxPostAsync(session.Client, $"{QuickTexts}?handler=Add", Token(page), ("Shortcut", shortcut), ("Body", "Synthetic: one copy"));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Equal([(shortcut, "Synthetic: one copy", 1)], await apps.RowsAsync(shortcut));
        }
        finally
        {
            apps.PrimaryDown = false;
        }
    }
}
