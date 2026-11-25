using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// The expansion plan's step 3: what the database lets each account do with inbox items, as the runtime roles, in SQL
// of its own (db/policies/10-grants.sql, 30-rls.sql and 70-inbox.sql). An item is read and marked done by its
// recipient alone; it is marked once, at the database's time, and nothing else about it changes; no runtime role
// inserts or deletes one yet; and the write audit records the change.
public class InboxSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    const string Insert = """
        INSERT INTO ehr.inbox_items (id, recipient_id, kind, patient_id, summary, created_at)
        VALUES (@id, @recipient, 'message', @patient, 'Synthetic: a message to read', now())
        """;

    async Task<(Guid Recipient, Guid Patient, Guid Item)> ItemAsync(string role = StaffRoles.Clinician)
    {
        var owner = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, owner);
        var recipient = role == StaffRoles.Clinician ? owner : await Accounts.StaffAsync(database, role);
        var item = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null, Insert, ("id", item), ("recipient", recipient), ("patient", patient));
        return (recipient, patient, item);
    }

    Task<List<string>> AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.QueryAsync(database.SuperuserConnectionString, null, sql, parameters);

    [Theory]
    [InlineData(StaffRoles.Clinician)]
    [InlineData(StaffRoles.Admin)]
    public async Task An_item_is_read_by_its_recipient_and_nobody_else(string role)
    {
        var (recipient, patient, item) = await ItemAsync(role);
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var patientAccount = await Accounts.PatientAccountAsync(database, patient);
        const string read = "SELECT count(*) FROM ehr.inbox_items WHERE id = @id";

        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal(["1"], await Accounts.QueryAsync(connection, recipient, read, ("id", item)));
            foreach (var other in new Guid?[] { colleague, admin, patientAccount, null, Guid.NewGuid() })
            {
                Assert.Equal(["0"], await Accounts.QueryAsync(connection, other, read, ("id", item)));
            }
        }
    }

    [Fact]
    public async Task A_recipient_whose_account_is_disabled_reads_nothing()
    {
        var (recipient, _, item) = await ItemAsync();
        await Accounts.LockOutAsync(database, recipient);

        Assert.Equal(["0"], await Accounts.QueryAsync(App, recipient, "SELECT count(*) FROM ehr.inbox_items WHERE id = @id", ("id", item)));
    }

    [Fact]
    public async Task The_recipient_marks_an_item_done_once_and_the_database_sets_the_time_and_audits_it()
    {
        var (recipient, patient, item) = await ItemAsync();

        await Accounts.ExecuteAsync(App, recipient, "UPDATE ehr.inbox_items SET done_at = '2000-01-01' WHERE id = @id", ("id", item));

        Assert.Equal(["true"], await AsSuperuserAsync("SELECT (done_at > now() - interval '1 minute')::text FROM ehr.inbox_items WHERE id = @id", ("id", item)));
        Assert.Equal([$"update {recipient} {patient}"], await AsSuperuserAsync(
            "SELECT action || ' ' || actor_id || ' ' || patient_id FROM ehr.audit_events WHERE table_name = 'inbox_items' AND row_id = @id AND action = 'update'",
            ("id", item)));

        // Not twice, and never reopened.
        foreach (var sql in new[] { "UPDATE ehr.inbox_items SET done_at = now() WHERE id = @id", "UPDATE ehr.inbox_items SET done_at = NULL WHERE id = @id" })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, recipient, sql, ("id", item)));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
    }

    [Fact]
    public async Task Nobody_else_marks_an_item_done_and_nothing_else_about_it_changes()
    {
        var (recipient, patient, item) = await ItemAsync();
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var patientAccount = await Accounts.PatientAccountAsync(database, patient);

        // Row-level security shows them no item to update.
        foreach (var other in new Guid?[] { colleague, admin, patientAccount, null })
        {
            await Accounts.ExecuteAsync(App, other, "UPDATE ehr.inbox_items SET done_at = now() WHERE id = @id", ("id", item));
        }

        Assert.Equal(["true"], await AsSuperuserAsync("SELECT (done_at IS NULL)::text FROM ehr.inbox_items WHERE id = @id", ("id", item)));

        // The grant is on done_at alone; the cloud app's role and every other statement are refused.
        foreach (var (connection, sql) in new[]
        {
            (App, "UPDATE ehr.inbox_items SET summary = 'changed' WHERE id = @id"),
            (App, "UPDATE ehr.inbox_items SET recipient_id = recipient_id, done_at = now() WHERE id = @id"),
            (App, "DELETE FROM ehr.inbox_items WHERE id = @id"),
            (App, "INSERT INTO ehr.inbox_items (id, recipient_id, kind, summary, created_at) VALUES (gen_random_uuid(), @id, 'message', 'x', now())"),
            (Read, "UPDATE ehr.inbox_items SET done_at = now() WHERE id = @id"),
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(connection, recipient, sql, ("id", item)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        // Even the owner can't edit an item while marking it.
        var edited = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "UPDATE ehr.inbox_items SET done_at = now(), summary = 'changed' WHERE id = @id", ("id", item)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, edited.SqlState);
    }

    [Fact]
    public async Task An_item_is_one_of_the_known_kinds()
    {
        var (recipient, _, _) = await ItemAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "INSERT INTO ehr.inbox_items (id, recipient_id, kind, summary, created_at) VALUES (gen_random_uuid(), @id, 'reminder', 'x', now())", ("id", recipient)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }
}
