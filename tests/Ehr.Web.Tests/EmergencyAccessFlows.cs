using Ehr.Data.Accounts;
using Ehr.Testing;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Reviewing emergency access (the expansion plan's step 18; UC 24): helpers for its test cases, one file each in
// TestCases/UC24, and for TC-UC12-08 and TC-UC15-09. What the database refuses whatever the app does is in
// Ehr.Security.Tests (EmergencyAccessReviewSecurityTests).
static class EmergencyAccessFlows
{
    public const string Reason = "Synthetic: covering the ward tonight, asked by the charge nurse.";

    public const string Note = "Synthetic: checked the rota, the clinician was covering the ward.";

    public sealed record Access(Guid Id, Guid Patient, StaffAccount Clinician, Session Session);

    // A clinician opens the chart of a patient off their panel through the reason page, as UC 12 has it, then views it
    // once more. The patient's name is family name "Reviewedchart" and a number.
    public static async Task<Access> GrantAsync(WebApps apps, string clinicianName, WebApplicationFactory<Program>? app = null, StaffAccount? clinician = null)
    {
        clinician ??= await apps.CreateStaffAsync(clinicianName, StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Synthetic", "Reviewedchart");
        var session = await SignInAsync(app ?? apps.OnPrem, clinician);
        using (var opened = await PostFormAsync(session.Client, $"/patients/{patient}/reason", $"/patients/{patient}/reason", ("Reason", Reason)))
        {
            Assert.Equal($"/patients/{patient}", PathOf(opened));
        }

        var id = Guid.Parse((await QueryAsync(apps,
            "SELECT id::text FROM ehr.break_glass_accesses WHERE patient_id = @a AND clinician_id = @b", ("a", patient), ("b", clinician.Id))).Single());
        return new(id, patient, clinician, session);
    }

    // The access ends now, by the database's clock, as four hours would have it.
    public static Task EndAsync(WebApps apps, Guid access) => QueryAsync(apps,
        "UPDATE ehr.break_glass_accesses SET expires_at = clock_timestamp() - interval '1 millisecond' WHERE id = @a RETURNING id", ("a", access));

    // The review page's form, as it posts it.
    public static Task<HttpResponseMessage> ReviewAsync(HttpClient client, Guid access, string? outcome, string note) =>
        outcome is null
            ? PostFormAsync(client, $"/admin/emergency-access/{access}", $"/admin/emergency-access/{access}", ("Note", note))
            : PostFormAsync(client, $"/admin/emergency-access/{access}", $"/admin/emergency-access/{access}", ("Outcome", outcome), ("Note", note));

    // Each administrator holding an item for the access, and whether it is done.
    public static async Task<Dictionary<Guid, bool>> ItemsAsync(WebApps apps, Guid access) =>
        (await QueryAsync(apps,
            "SELECT recipient_id::text || ' ' || (done_at IS NOT NULL)::text FROM ehr.inbox_items WHERE source_table = 'break_glass_accesses' AND source_id = @a",
            ("a", access)))
        .Select(row => row.Split(' ')).ToDictionary(row => Guid.Parse(row[0]), row => bool.Parse(row[1]));

    public static async Task<List<string>> ReviewsAsync(WebApps apps, Guid access) => await QueryAsync(apps,
        "SELECT reviewer_id || ' ' || outcome || ' ' || note FROM ehr.emergency_access_reviews WHERE id = @a", ("a", access));

    // The first column of each row, as the superuser.
    public static async Task<List<string>> QueryAsync(WebApps apps, string sql, params (string Name, object Value)[] parameters)
    {
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(sql, db);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        while (await reader.ReadAsync(Cancel))
        {
            rows.Add(reader.IsDBNull(0) ? "" : reader.GetValue(0).ToString()!);
        }

        return rows;
    }

    // The migration job's run of the step's policies, whose last statement is the sweep of waiting accesses.
    public static Task SweepAsync(WebApps apps) => MigratedDatabase.ExecuteAsync(apps.Database.OwnerConnectionString,
        File.ReadAllText(RepositoryRoot.Combine("db/policies/72-emergency-access-review.sql")));
}
