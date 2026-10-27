using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// db/policies/30-rls.sql, as each kind of account, through both runtime roles. CI gate from AGENTS.md: querying a
// PHI table without app.user_id set returns zero rows.
public class RowLevelSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    [Fact]
    public async Task Without_a_user_a_phi_table_returns_no_rows()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);

        foreach (var connectionString in new[] { App, Read })
        {
            Assert.Equal(["0"], await CountAsync(connectionString, null, patient));
            // A user ID that is no account at all: not staff, not a patient.
            Assert.Equal(["0"], await CountAsync(connectionString, Guid.CreateVersion7(), patient));
        }
    }

    [Fact]
    public async Task A_clinician_sees_every_patient_and_an_admin_sees_none()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var first = await Accounts.PatientAsync(database, clinician);
        var second = await Accounts.PatientAsync(database, clinician);

        foreach (var connectionString in new[] { App, Read })
        {
            Assert.Equal(["1"], await CountAsync(connectionString, clinician, first));
            Assert.Equal(["1"], await CountAsync(connectionString, clinician, second));
            Assert.Equal(["0"], await CountAsync(connectionString, admin, first));
        }
    }

    [Fact]
    public async Task A_patient_sees_only_their_own_record()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var own = await Accounts.PatientAsync(database, clinician);
        var other = await Accounts.PatientAsync(database, clinician);
        var account = await Accounts.PatientAccountAsync(database, own);

        foreach (var connectionString in new[] { App, Read })
        {
            Assert.Equal(["1"], await CountAsync(connectionString, account, own));
            Assert.Equal(["0"], await CountAsync(connectionString, account, other));
        }
    }

    [Fact]
    public async Task A_patient_cannot_change_patients()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var own = await Accounts.PatientAsync(database, clinician);
        var account = await Accounts.PatientAccountAsync(database, own);

        // The row is visible to them, and the update still fails: no policy lets a patient write.
        var updated = await Accounts.QueryAsync(App, account,
            "UPDATE ehr.patients SET family_name = 'Changed' WHERE id = @id RETURNING id", ("id", own));
        Assert.Empty(updated);

        var inserting = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, account,
            """
            INSERT INTO ehr.patients (id, mrn, given_name, family_name, birth_date, gender, version, updated_at)
            VALUES (@id, @mrn, 'New', 'Patient', '1980-01-01', 'unknown', 1, now())
            """,
            ("id", Guid.CreateVersion7()), ("mrn", Guid.NewGuid().ToString("N")[..12])));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, inserting.SqlState);
    }

    [Fact]
    public async Task A_clinician_keeps_their_own_panel_and_reads_every_panel()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var patient = await Accounts.PatientAsync(database, clinician);
        var assignment = await Accounts.AssignAsync(database, clinician, patient);

        const string count = "SELECT count(*) FROM ehr.patient_assignments WHERE id = @id";
        foreach (var connectionString in new[] { App, Read })
        {
            Assert.Equal(["1"], await Accounts.QueryAsync(connectionString, clinician, count, ("id", assignment)));
            Assert.Equal(["1"], await Accounts.QueryAsync(connectionString, colleague, count, ("id", assignment)));
            Assert.Equal(["0"], await Accounts.QueryAsync(connectionString, admin, count, ("id", assignment)));
            Assert.Equal(["0"], await Accounts.QueryAsync(connectionString, null, count, ("id", assignment)));
        }

        // Only onto their own panel, and only their own assignments end.
        var assigningForAnother = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
            "INSERT INTO ehr.patient_assignments (id, patient_id, clinician_id, assigned_at) VALUES (@id, @patient, @clinician, now())",
            ("id", Guid.CreateVersion7()), ("patient", patient), ("clinician", colleague)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, assigningForAnother.SqlState);
        Assert.Empty(await Accounts.QueryAsync(App, colleague,
            "UPDATE ehr.patient_assignments SET ended_at = now() WHERE id = @id RETURNING id", ("id", assignment)));
        Assert.Equal([assignment.ToString()], await Accounts.QueryAsync(App, clinician,
            "UPDATE ehr.patient_assignments SET ended_at = now() WHERE id = @id RETURNING id", ("id", assignment)));
    }

    [Fact]
    public async Task A_patient_sees_who_their_clinicians_are_and_nobody_elses()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var own = await Accounts.PatientAsync(database, clinician);
        var other = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, own);
        await Accounts.AssignAsync(database, clinician, other);
        var account = await Accounts.PatientAccountAsync(database, own);

        const string count = "SELECT count(*) FROM ehr.patient_assignments WHERE patient_id = @id";
        foreach (var connectionString in new[] { App, Read })
        {
            Assert.Equal(["1"], await Accounts.QueryAsync(connectionString, account, count, ("id", own)));
            Assert.Equal(["0"], await Accounts.QueryAsync(connectionString, account, count, ("id", other)));
        }
    }

    [Fact]
    public async Task A_locked_out_clinician_sees_nothing()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);

        await Accounts.LockOutAsync(database, clinician);

        Assert.Equal(["0"], await CountAsync(App, clinician, patient));
    }

    [Fact]
    public async Task Only_an_admin_reads_the_audit_log()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var patient = await Accounts.PatientAsync(database, clinician);
        var account = await Accounts.PatientAccountAsync(database, patient);

        const string sql = "SELECT count(*) FROM ehr.audit_events WHERE patient_id = @id";
        foreach (var connectionString in new[] { App, Read })
        {
            Assert.Equal(["1"], await Accounts.QueryAsync(connectionString, admin, sql, ("id", patient)));
            Assert.Equal(["0"], await Accounts.QueryAsync(connectionString, clinician, sql, ("id", patient)));
            Assert.Equal(["0"], await Accounts.QueryAsync(connectionString, account, sql, ("id", patient)));
            Assert.Equal(["0"], await Accounts.QueryAsync(connectionString, null, sql, ("id", patient)));
        }
    }

    [Fact]
    public async Task The_app_role_records_read_events_for_the_current_user()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var patient = await Accounts.PatientAsync(database, clinician);

        // What the PHI reader will do on the on-prem site, in the transaction of the read.
        await Accounts.ExecuteAsync(App, clinician,
            "INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source) VALUES (uuidv7(), now(), @actor, 'read', 'patients', @id, @id, 'onprem')",
            ("actor", clinician), ("id", patient));

        Assert.Equal(["read"], await Accounts.QueryAsync(App, admin,
            "SELECT action FROM ehr.audit_events WHERE patient_id = @id AND action = 'read'", ("id", patient)));
    }

    static Task<List<string>> CountAsync(string connectionString, Guid? userId, Guid patientId) =>
        Accounts.QueryAsync(connectionString, userId, "SELECT count(*) FROM ehr.patients WHERE id = @id", ("id", patientId));
}
