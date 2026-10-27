using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// Rows and connections for the row-level security and audit tests. Accounts are inserted directly, as the superuser,
// the way sign-up will insert them: these tests are about what the database lets each account do, not about sign-in.
// Work as a runtime role runs in one transaction with the user set first, the way UserContextInterceptor sets it.
static class Accounts
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

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
            ("id", id), ("mrn", id.ToString("N")[..12]));
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

    static async Task SetUserAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid? userId)
    {
        await using var set = Command(connection, transaction, "SELECT set_config('app.user_id', @user_id, true)",
            ("user_id", userId?.ToString() ?? ""));
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
