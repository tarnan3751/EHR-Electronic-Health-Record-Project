using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// FR-18 (the expansion plan's step 5): what the database lets each account do with message threads and messages, as
// the runtime roles, in SQL of its own (db/policies/10-grants.sql, 30-rls.sql, 40-audit.sql and 90-messages.sql). A
// patient writes only in their own threads and reads only their own; a clinician writes only for a patient on their
// panel; who sent a message and when are the database's; nothing is edited or deleted; a message is marked read once,
// by the other side; a patient's message reaches each care-team clinician's inbox and a reply clears it; and an admin
// never gets a message's text.
public class MessageSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    const string Thread = "INSERT INTO ehr.message_threads (id, patient_id, subject, started_at) VALUES (@thread, @patient, 'Synthetic subject', '2000-01-01')";

    const string Message = """
        INSERT INTO ehr.messages (id, thread_id, patient_id, sender_kind, sender_id, body, sent_at, read_at)
        VALUES (@id, @thread, @patient, @kind, @sender, 'Synthetic: the text of a message', '2000-01-01', '2000-01-01')
        """;

    async Task<(Guid Clinician, Guid Patient, Guid Account)> CareAsync()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, patient);
        return (clinician, patient, await Accounts.PatientAccountAsync(database, patient));
    }

    Task SendAsync(Guid? user, Guid thread, Guid patient, string kind, Guid? sender = null, Guid? id = null) =>
        Accounts.ExecuteAsync(App, user, Message, ("id", id ?? Guid.CreateVersion7()), ("thread", thread), ("patient", patient), ("kind", kind),
            ("sender", sender ?? user ?? Guid.NewGuid()));

    async Task<Guid> ThreadAsync(Guid user, Guid patient)
    {
        var thread = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, user, Thread, ("thread", thread), ("patient", patient));
        return thread;
    }

    Task<List<string>> AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.QueryAsync(database.SuperuserConnectionString, null, sql, parameters);

    [Fact]
    public async Task A_patients_message_is_theirs_at_the_databases_time_and_reaches_each_care_team_inbox()
    {
        var (clinician, patient, account) = await CareAsync();
        var second = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, second, patient);
        var notOnTeam = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var thread = await ThreadAsync(account, patient);
        var id = Guid.CreateVersion7();

        // Claiming another sender, an old time and "already read" changes nothing.
        await SendAsync(account, thread, patient, "patient", sender: clinician, id: id);
        await SendAsync(account, thread, patient, "patient");

        Assert.Equal([$"{account} true true"], await AsSuperuserAsync(
            "SELECT sender_id || ' ' || (sent_at > now() - interval '1 minute') || ' ' || (read_at IS NULL) FROM ehr.messages WHERE id = @id", ("id", id)));
        Assert.Equal(["true"], await AsSuperuserAsync(
            "SELECT (last_message_at > started_at)::text FROM ehr.message_threads WHERE id = @id", ("id", thread)));
        // One open item each for the two on the care team, however many messages; none for anyone else. No text in it.
        Assert.Equal([$"{clinician} A patient sent a message", $"{second} A patient sent a message"], (await AsSuperuserAsync(
            "SELECT recipient_id || ' ' || summary FROM ehr.inbox_items WHERE source_id = @thread AND kind = 'message' AND done_at IS NULL", ("thread", thread))).Order());
        Assert.Equal(["0"], await Accounts.QueryAsync(App, notOnTeam, "SELECT count(*) FROM ehr.inbox_items"));

        // A reply from one of them clears it for both, and is theirs.
        await SendAsync(second, thread, patient, "staff");
        Assert.Equal(["0"], await AsSuperuserAsync("SELECT count(*) FROM ehr.inbox_items WHERE source_id = @thread AND done_at IS NULL", ("thread", thread)));
        Assert.Equal([$"{second}"], await AsSuperuserAsync("SELECT sender_id::text FROM ehr.messages WHERE thread_id = @thread AND sender_kind = 'staff'", ("thread", thread)));
    }

    [Fact]
    public async Task Only_the_patient_and_their_care_team_write_and_only_as_themselves()
    {
        var (clinician, patient, account) = await CareAsync();
        var thread = await ThreadAsync(account, patient);
        var otherClinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var otherPatient = await Accounts.PatientAsync(database, otherClinician);
        var otherAccount = await Accounts.PatientAccountAsync(database, otherPatient);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);

        // Another patient, a clinician off the panel, an admin, nobody: no thread and no message for this patient.
        foreach (var (user, kind) in new (Guid?, string)[] { (otherAccount, "patient"), (otherClinician, "staff"), (admin, "staff"), (null, "patient") })
        {
            // Refused by row-level security, or, for someone who can't even see the thread, by the trigger that looks for it.
            var message = await Assert.ThrowsAsync<PostgresException>(() => SendAsync(user, thread, patient, kind));
            Assert.Contains(message.SqlState, new[] { PostgresErrorCodes.InsufficientPrivilege, PostgresErrorCodes.CheckViolation });
            var started = await Assert.ThrowsAsync<PostgresException>(() =>
                Accounts.ExecuteAsync(App, user, Thread, ("thread", Guid.CreateVersion7()), ("patient", patient)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, started.SqlState);
        }

        // Not as the other side: a patient can't send as staff, nor a clinician as the patient.
        foreach (var (user, kind) in new[] { (account, "staff"), (clinician, "patient") })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => SendAsync(user, thread, patient, kind));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        // Not into another patient's thread under one's own name, and not an empty message.
        var theirs = await ThreadAsync(otherAccount, otherPatient);
        var crossed = await Assert.ThrowsAsync<PostgresException>(() => SendAsync(account, theirs, patient, "patient"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, crossed.SqlState);
        var empty = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, account,
            "INSERT INTO ehr.messages (id, thread_id, patient_id, sender_kind, body) VALUES (gen_random_uuid(), @thread, @patient, 'patient', '   ')",
            ("thread", thread), ("patient", patient)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, empty.SqlState);

        // The cloud app's role writes nothing.
        var readRole = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(Read, account, Message,
            ("id", Guid.CreateVersion7()), ("thread", thread), ("patient", patient), ("kind", "patient"), ("sender", account)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, readRole.SqlState);
        Assert.Equal(["0"], await AsSuperuserAsync("SELECT count(*) FROM ehr.messages WHERE thread_id = @thread", ("thread", thread)));
    }

    [Fact]
    public async Task A_patient_reads_only_their_own_threads_and_clinicians_read_all()
    {
        var (clinician, patient, account) = await CareAsync();
        var thread = await ThreadAsync(account, patient);
        await SendAsync(account, thread, patient, "patient");
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var otherPatient = await Accounts.PatientAsync(database, colleague);
        var otherAccount = await Accounts.PatientAccountAsync(database, otherPatient);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        const string read = "SELECT (SELECT count(*) FROM ehr.message_threads WHERE id = @id) + (SELECT count(*) FROM ehr.messages WHERE thread_id = @id)";

        foreach (var connection in new[] { App, Read })
        {
            foreach (var allowed in new[] { account, clinician, colleague })
            {
                Assert.Equal(["2"], await Accounts.QueryAsync(connection, allowed, read, ("id", thread)));
            }

            foreach (var refused in new Guid?[] { otherAccount, admin, null, Guid.NewGuid() })
            {
                Assert.Equal(["0"], await Accounts.QueryAsync(connection, refused, read, ("id", thread)));
            }
        }
    }

    [Fact]
    public async Task A_message_is_never_edited_or_deleted_and_is_marked_read_once_by_the_other_side()
    {
        var (clinician, patient, account) = await CareAsync();
        var thread = await ThreadAsync(account, patient);
        var fromPatient = Guid.CreateVersion7();
        var fromStaff = Guid.CreateVersion7();
        await SendAsync(account, thread, patient, "patient", id: fromPatient);
        await SendAsync(clinician, thread, patient, "staff", id: fromStaff);
        var offPanel = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        const string mark = "UPDATE ehr.messages SET read_at = '2000-01-01' WHERE id = @id";

        // Not by the sender's own side, and not by a clinician off the panel: no row to update.
        await Accounts.ExecuteAsync(App, account, mark, ("id", fromPatient));
        await Accounts.ExecuteAsync(App, clinician, mark, ("id", fromStaff));
        await Accounts.ExecuteAsync(App, offPanel, mark, ("id", fromPatient));
        Assert.Equal(["2"], await AsSuperuserAsync("SELECT count(*) FROM ehr.messages WHERE thread_id = @thread AND read_at IS NULL", ("thread", thread)));

        // By the other side, at the database's time, once.
        await Accounts.ExecuteAsync(App, clinician, mark, ("id", fromPatient));
        await Accounts.ExecuteAsync(App, account, mark, ("id", fromStaff));
        Assert.Equal(["true", "true"], await AsSuperuserAsync(
            "SELECT (read_at > now() - interval '1 minute')::text FROM ehr.messages WHERE thread_id = @thread", ("thread", thread)));
        var again = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician, mark, ("id", fromPatient)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, again.SqlState);

        foreach (var sql in new[]
        {
            "UPDATE ehr.messages SET body = 'changed' WHERE id = @id",
            "DELETE FROM ehr.messages WHERE id = @id",
            "UPDATE ehr.message_threads SET subject = 'changed' WHERE id = @thread",
            "DELETE FROM ehr.message_threads WHERE id = @thread",
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician, sql, ("id", fromPatient), ("thread", thread)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
    }

    // The write audit keeps every message; an admin sees that one was written and by whom, never its text or subject.
    [Fact]
    public async Task An_admin_never_gets_a_messages_text_from_the_audit_log()
    {
        var (_, patient, account) = await CareAsync();
        var thread = await ThreadAsync(account, patient);
        await SendAsync(account, thread, patient, "patient");
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);

        // The thread, its latest-message time moving on, and the message: each listed, none with its values.
        Assert.Equal(["message_threads insert hidden", "message_threads update hidden", "messages insert hidden"], (await Accounts.QueryAsync(App, admin,
            """
            SELECT e.table_name || ' ' || e.action || ' ' || coalesce((SELECT d.details::text FROM ehr.shown_audit_details(ARRAY[e.id]) d), 'hidden')
            FROM ehr.audit_events e WHERE e.patient_id = @patient AND e.table_name IN ('messages', 'message_threads') AND e.actor_id = @actor
            """,
            ("patient", patient), ("actor", account))).Order(StringComparer.Ordinal));
        Assert.Contains("Synthetic: the text of a message", Assert.Single(await AsSuperuserAsync(
            "SELECT details::text FROM ehr.audit_events WHERE table_name = 'messages' AND patient_id = @patient", ("patient", patient))));
    }
}
