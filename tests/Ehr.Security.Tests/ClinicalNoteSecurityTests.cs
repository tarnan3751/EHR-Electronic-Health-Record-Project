using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// Direct SQL as runtime roles: these invariants must hold even if an application check is missed.
public class ClinicalNoteSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);
    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    const string InsertNote = """
        INSERT INTO ehr.clinical_notes (id, draft_id, patient_id, draft_version, history, assessment, plan, signer_id, signed_at)
        VALUES (@note, @id, @patient, @version, @history, 'Synthetic assessment', 'Synthetic plan', @spoofed, '2000-01-01')
        """;

    async Task<(Guid Clinician, Guid Patient, Guid Draft)> DraftAsync()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, patient);
        var draft = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician,
            """
            INSERT INTO ehr.clinical_drafts
                (id, patient_id, clinician_id, body, version, history, assessment, plan, structured_version, has_unreviewed_proposals)
            VALUES (@id, @patient, @owner, 'Synthetic plain text', 1, 'Synthetic history', 'Synthetic assessment', 'Synthetic plan', 1, false)
            """, ("id", draft), ("patient", patient), ("owner", clinician));
        return (clinician, patient, draft);
    }

    Task SignAsync(Guid clinician, Guid patient, Guid draft, long version = 1, string history = "Synthetic history", Guid? spoofed = null) =>
        Accounts.ExecuteAsync(App, clinician, InsertNote, ("note", Guid.CreateVersion7()), ("id", draft), ("patient", patient),
            ("version", version), ("history", history), ("spoofed", spoofed ?? clinician));

    [Fact]
    public async Task Drafts_are_private_in_both_runtime_roles_and_cannot_be_reassigned()
    {
        var (clinician, patient, draft) = await DraftAsync();
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var patientAccount = await Accounts.PatientAccountAsync(database, patient);
        await Accounts.AssignAsync(database, colleague, patient);
        const string read = "SELECT count(*) FROM ehr.clinical_drafts WHERE id = @id";
        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal(["1"], await Accounts.QueryAsync(connection, clinician, read, ("id", draft)));
            foreach (var denied in new Guid?[] { colleague, admin, patientAccount, null, Guid.NewGuid() })
                Assert.Equal(["0"], await Accounts.QueryAsync(connection, denied, read, ("id", draft)));
        }
        Assert.Empty(await Accounts.QueryAsync(App, colleague,
            "UPDATE ehr.clinical_drafts SET body = 'Refused', version = version + 1 WHERE id = @id RETURNING id", ("id", draft)));
        await Assert.ThrowsAsync<PostgresException>(() => SignAsync(colleague, patient, draft));
        await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
            "UPDATE ehr.clinical_drafts SET clinician_id = @other, version = version + 1 WHERE id = @id", ("id", draft), ("other", colleague)));
        await Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.patient_assignments SET ended_at = now() WHERE patient_id = @id", ("id", patient));
        Assert.Equal(["0"], await Accounts.QueryAsync(App, clinician, read, ("id", draft)));
        await Assert.ThrowsAsync<PostgresException>(() => SignAsync(clinician, patient, draft));
    }

    [Fact]
    public async Task Signed_records_have_server_identity_time_audit_and_no_runtime_mutation_path()
    {
        var (clinician, patient, draft) = await DraftAsync();
        var spoofed = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await SignAsync(clinician, patient, draft, spoofed: spoofed);
        Assert.Equal([clinician.ToString()], await Accounts.QueryAsync(App, clinician,
            "SELECT signer_id FROM ehr.clinical_notes WHERE draft_id = @id AND signed_at > now() - interval '1 minute'", ("id", draft)));
        Assert.Equal(["1"], await Accounts.QueryAsync(database.SuperuserConnectionString, null,
            "SELECT count(*) FROM ehr.audit_events WHERE action = 'insert' AND table_name = 'clinical_notes' AND patient_id = @id AND actor_id = @actor",
            ("id", patient), ("actor", clinician)));
        await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
            "UPDATE ehr.clinical_drafts SET body = 'Refused', version = version + 1 WHERE id = @id", ("id", draft)));
        await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
            "UPDATE ehr.clinical_notes SET history = 'Refused' WHERE draft_id = @id", ("id", draft)));
        await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
            "DELETE FROM ehr.clinical_notes WHERE draft_id = @id", ("id", draft)));
        // Even a privileged maintenance write cannot silently alter a signed clinical record.
        await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "UPDATE ehr.clinical_notes SET history = 'Refused' WHERE draft_id = @id", ("id", draft)));
        await Assert.ThrowsAsync<PostgresException>(() => SignAsync(clinician, patient, draft, version: 2));
        Assert.Equal(["1"], await Accounts.QueryAsync(App, clinician,
            "SELECT count(*) FROM ehr.clinical_notes WHERE draft_id = @id", ("id", draft)));
    }

    [Fact]
    public async Task An_update_waiting_on_a_signature_lock_cannot_modify_the_finalized_draft()
    {
        var (clinician, patient, draft) = await DraftAsync();
        await using var signer = new NpgsqlConnection(App);
        await using var editor = new NpgsqlConnection(App);
        await signer.OpenAsync(TestContext.Current.CancellationToken);
        await editor.OpenAsync(TestContext.Current.CancellationToken);
        await using var signing = await signer.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var editing = await editor.BeginTransactionAsync(TestContext.Current.CancellationToken);
        foreach (var (connection, transaction) in new[] { (signer, signing), (editor, editing) })
        {
            await using var set = new NpgsqlCommand("SELECT set_config('app.user_id', @actor, true)", connection, transaction);
            set.Parameters.AddWithValue("actor", clinician.ToString());
            await set.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var sign = new NpgsqlCommand(InsertNote, signer, signing);
        sign.Parameters.AddWithValue("note", Guid.CreateVersion7());
        sign.Parameters.AddWithValue("id", draft);
        sign.Parameters.AddWithValue("patient", patient);
        sign.Parameters.AddWithValue("version", 1L);
        sign.Parameters.AddWithValue("history", "Synthetic history");
        sign.Parameters.AddWithValue("spoofed", clinician);
        await sign.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await using var update = new NpgsqlCommand(
            "UPDATE ehr.clinical_drafts SET body = 'Must not overwrite', version = version + 1 WHERE id = @id", editor, editing);
        update.Parameters.AddWithValue("id", draft);
        var waiting = update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        // Observe the competing writer actually waiting, rather than relying on request scheduling or sleeps.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var blocked = await Accounts.QueryAsync(database.SuperuserConnectionString, null,
                "SELECT cardinality(pg_blocking_pids(@pid))", ("pid", editor.ProcessID));
            if (blocked[0] != "0") break;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        Assert.False(waiting.IsCompleted);
        await signing.CommitAsync(TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<PostgresException>(async () => await waiting);
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(["Synthetic plain text"], await Accounts.QueryAsync(App, clinician,
            "SELECT body FROM ehr.clinical_drafts WHERE id = @id", ("id", draft)));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("stale")]
    [InlineData("incomplete")]
    [InlineData("tampered")]
    [InlineData("wrong patient")]
    public async Task Database_refuses_unreviewed_stale_incomplete_and_fabricated_signed_content(string kind)
    {
        var (clinician, patient, draft) = await DraftAsync();
        if (kind == "pending")
            await Accounts.ExecuteAsync(App, clinician,
                "UPDATE ehr.clinical_drafts SET has_unreviewed_proposals = true, version = 2, structured_version = 2 WHERE id = @id", ("id", draft));
        if (kind == "incomplete")
            await Accounts.ExecuteAsync(App, clinician,
                "UPDATE ehr.clinical_drafts SET plan = '  ', version = 2, structured_version = 2 WHERE id = @id", ("id", draft));
        var targetPatient = kind == "wrong patient" ? await Accounts.PatientAsync(database, clinician) : patient;
        var error = await Assert.ThrowsAsync<PostgresException>(() => SignAsync(clinician, targetPatient, draft,
            version: kind is "pending" or "incomplete" or "stale" ? 2 : 1,
            history: kind == "tampered" ? "Fabricated" : "Synthetic history"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(["0"], await Accounts.QueryAsync(App, clinician,
            "SELECT count(*) FROM ehr.clinical_notes WHERE draft_id = @id", ("id", draft)));
    }

    [Fact]
    public async Task Signed_notes_follow_clinician_chart_access_and_never_expose_drafts_to_admins_or_patients()
    {
        var (clinician, patient, draft) = await DraftAsync();
        await SignAsync(clinician, patient, draft);
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var patientAccount = await Accounts.PatientAccountAsync(database, patient);
        var locked = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.LockOutAsync(database, locked);
        const string read = "SELECT count(*) FROM ehr.clinical_notes WHERE draft_id = @id";
        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal(["1"], await Accounts.QueryAsync(connection, colleague, read, ("id", draft)));
            foreach (var denied in new Guid?[] { admin, patientAccount, locked, null, Guid.NewGuid() })
                Assert.Equal(["0"], await Accounts.QueryAsync(connection, denied, read, ("id", draft)));
        }
    }

    [Fact]
    public async Task Read_only_database_connections_cannot_sign_and_leave_no_signed_record()
    {
        var (clinician, patient, draft) = await DraftAsync();
        var readOnly = new NpgsqlConnectionStringBuilder(App) { Options = "-c default_transaction_read_only=on" }.ConnectionString;
        var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(readOnly, clinician, InsertNote,
            ("note", Guid.CreateVersion7()), ("id", draft), ("patient", patient), ("version", 1L), ("history", "Synthetic history"), ("spoofed", clinician)));
        Assert.Equal(PostgresErrorCodes.ReadOnlySqlTransaction, error.SqlState);
        Assert.Equal(["0"], await Accounts.QueryAsync(App, clinician,
            "SELECT count(*) FROM ehr.clinical_notes WHERE draft_id = @id", ("id", draft)));
        await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(Read, clinician, InsertNote,
            ("note", Guid.CreateVersion7()), ("id", draft), ("patient", patient), ("version", 1L), ("history", "Synthetic history"), ("spoofed", clinician)));
    }
}
