using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;
using static Ehr.Testing.TestRun;

namespace Ehr.Security.Tests;

// Rows and connections for the row-level security and audit tests. Accounts are inserted directly, as the superuser,
// the way sign-up will insert them: these tests are about what the database lets each account do, not about sign-in.
// Work as a runtime role runs in one transaction with the user set first, the way UserContextInterceptor sets it.
static class Accounts
{
    public static async Task<Guid> StaffAsync(MigratedDatabase database, params string[] roles)
    {
        var id = Guid.CreateVersion7();
        var email = $"{id:N}@example.com";
        await ExecuteAsync(database.SuperuserConnectionString, null,
            """
            INSERT INTO ehr.staff_users (id, user_name, normalized_user_name, email, normalized_email, email_confirmed,
                phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count, display_name, roles,
                must_change_password)
            VALUES (@id, @email, upper(@email), @email, upper(@email), true, false, false, true, 0, 'Test Staff', @roles, false)
            """,
            ("id", id), ("email", email), ("roles", roles));
        return id;
    }

    public static async Task<Guid> PatientAccountAsync(MigratedDatabase database, Guid patientId)
    {
        var id = Guid.CreateVersion7();
        var email = $"{id:N}@example.com";
        await ExecuteAsync(database.SuperuserConnectionString, null,
            """
            INSERT INTO ehr.patient_users (id, user_name, normalized_user_name, email, normalized_email, email_confirmed,
                phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count, patient_id)
            VALUES (@id, @email, upper(@email), @email, upper(@email), true, false, false, true, 0, @patient)
            """,
            ("id", id), ("email", email), ("patient", patientId));
        return id;
    }

    // A proxy-only portal account (step 6b): confirmed, with no patient record of its own. Its ID and its address.
    public static async Task<(Guid Id, string Email)> ProxyOnlyAccountAsync(MigratedDatabase database)
    {
        var id = Guid.CreateVersion7();
        var email = $"{id:N}@example.com";
        await ExecuteAsync(database.SuperuserConnectionString, null,
            """
            INSERT INTO ehr.patient_users (id, user_name, normalized_user_name, email, normalized_email, email_confirmed,
                phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count, patient_id, display_name)
            VALUES (@id, @email, upper(@email), @email, upper(@email), true, false, false, true, 0, NULL, 'Synthetic Proxy')
            """,
            ("id", id), ("email", email));
        return (id, email);
    }

    // How an account is disabled: locked out indefinitely.
    public static Task LockOutAsync(MigratedDatabase database, Guid staffId) =>
        ExecuteAsync(database.SuperuserConnectionString, null,
            "UPDATE ehr.staff_users SET lockout_end = now() + interval '100 years' WHERE id = @id", ("id", staffId));

    // A patient inserted by a clinician through the app role, as the app will.
    public static async Task<Guid> PatientAsync(MigratedDatabase database, Guid clinicianId)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword), clinicianId,
            """
            INSERT INTO ehr.patients (id, mrn, given_name, family_name, birth_date, gender, version, updated_at)
            VALUES (@id, @mrn, 'Test', 'Patient', '1980-01-01', 'unknown', 1, now())
            """,
            ("id", id), ("mrn", id.ToString("N")[^12..]));
        return id;
    }

    // A patient put on a clinician's panel, by that clinician through the app role, as the app will.
    public static async Task<Guid> AssignAsync(MigratedDatabase database, Guid clinicianId, Guid patientId)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword), clinicianId,
            "INSERT INTO ehr.patient_assignments (id, patient_id, clinician_id, assigned_at) VALUES (@id, @patient, @clinician, now())",
            ("id", id), ("patient", patientId), ("clinician", clinicianId));
        return id;
    }

    // A clinician and a patient on the clinician's panel, made as above.
    public static async Task<(Guid Clinician, Guid Patient)> OnPanelAsync(MigratedDatabase database)
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, patient);
        return (clinician, patient);
    }

    // The chart's other tables, each with the smallest row it takes. Nothing at runtime writes them yet, so rows are
    // inserted as the superuser, the way a seed would.
    public static readonly Dictionary<string, string> ChartTables = new()
    {
        ["allergy_intolerances"] = "INSERT INTO ehr.allergy_intolerances (id, patient_id, display, clinical_status, recorded_at) VALUES (@id, @patient, 'Penicillin', 'active', now())",
        ["medication_statements"] = "INSERT INTO ehr.medication_statements (id, patient_id, display, status) VALUES (@id, @patient, 'Lisinopril 10 mg tablet', 'active')",
        ["conditions"] = "INSERT INTO ehr.conditions (id, patient_id, display, clinical_status) VALUES (@id, @patient, 'Essential (primary) hypertension', 'active')",
        ["encounters"] = "INSERT INTO ehr.encounters (id, patient_id, status, class_code, period_start) VALUES (@id, @patient, 'finished', 'AMB', now())",
        // A released result, the kind its patient may see (30-rls.sql).
        ["observations"] = "INSERT INTO ehr.observations (id, patient_id, display, status, effective_at, issued_at) VALUES (@id, @patient, 'Hemoglobin A1c', 'final', now(), now())",
    };

    public static async Task<Guid> ChartRowAsync(MigratedDatabase database, string table, Guid patientId)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(database.SuperuserConnectionString, null, ChartTables[table], ("id", id), ("patient", patientId));
        return id;
    }

    public static async Task ExecuteAsync(string connectionString, Guid? userId, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Cancel);
        await using var transaction = await connection.BeginTransactionAsync(Cancel);
        await SetUserAsync(connection, transaction, userId);
        await using var command = Command(connection, transaction, sql, parameters);
        await command.ExecuteNonQueryAsync(Cancel);
        await transaction.CommitAsync(Cancel);
    }

    // The first column of every row, as text.
    public static async Task<List<string>> QueryAsync(string connectionString, Guid? userId, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Cancel);
        await using var transaction = await connection.BeginTransactionAsync(Cancel);
        await SetUserAsync(connection, transaction, userId);
        await using var command = Command(connection, transaction, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        var rows = new List<string>();
        while (await reader.ReadAsync(Cancel))
        {
            rows.Add(reader.IsDBNull(0) ? "" : reader.GetValue(0).ToString()!);
        }

        return rows;
    }

    // The proxy grant the statements that follow say they act under (FR-19), as the app's UserContextInterceptor sends
    // it beside the user: set it, run the statements, and dispose what this returns.
    public static IDisposable ActingUnder(Guid grant)
    {
        Acting.Value = grant;
        return new StopActing();
    }

    static readonly AsyncLocal<Guid?> Acting = new();

    sealed class StopActing : IDisposable
    {
        public void Dispose() => Acting.Value = null;
    }

    static async Task SetUserAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid? userId)
    {
        await using var set = Command(connection, transaction,
            "SELECT set_config('app.user_id', @user_id, true), set_config('app.acting_grant', @grant, true)",
            ("user_id", userId?.ToString() ?? ""), ("grant", Acting.Value?.ToString() ?? ""));
        await set.ExecuteNonQueryAsync(Cancel);
    }

    static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }
}
