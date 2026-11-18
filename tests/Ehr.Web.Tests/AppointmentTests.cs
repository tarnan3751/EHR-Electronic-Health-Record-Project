using System.Globalization;
using Ehr.Data.Accounts;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Appointments (FR-17; the expansion plan's step 4): helpers for the test cases in TestCases/UC16 (scheduling) and
// TestCases/UC6 (the portal's list). What the database refuses whatever the app does is in Ehr.Security.Tests
// (AppointmentSecurityTests).
static class AppointmentTests
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // A day well ahead, so no test's time has passed; each test takes its own day so bookings don't collide.
    public static string Day(int offset) => DateTime.UtcNow.Date.AddDays(30 + offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string NewPath(Guid patient) => $"/patients/{patient}/appointments/new";

    public static async Task<(StaffAccount Clinician, Guid Patient, Session Session)> ClinicianAsync(WebApps apps, string name)
    {
        var clinician = await apps.CreateStaffAsync(name, StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Synthetic", "Booked");
        await apps.AssignAsync(clinician, patient);
        return (clinician, patient, await SignInAsync(apps.OnPrem, clinician));
    }

    // "Book appointment", as the form posts it.
    public static Task<HttpResponseMessage> BookAsync(HttpClient client, Guid patient, Guid clinician, string day, string time, string reason, int minutes = 20) =>
        PostFormAsync(client, NewPath(patient), NewPath(patient),
            ("Clinician", clinician), ("Day", day), ("Time", time), ("Minutes", minutes), ("Reason", reason));

    public static async Task<List<string>> RowsAsync(WebApps apps, Guid patient, string expression = "status")
    {
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand($"SELECT {expression} FROM ehr.appointments WHERE patient_id = @patient ORDER BY starts_at, id", db);
        command.Parameters.AddWithValue("patient", patient);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        while (await reader.ReadAsync(Cancel))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    public static async Task<Guid> IdAsync(WebApps apps, Guid patient) => Guid.Parse((await RowsAsync(apps, patient, "id::text")).Single());
}
