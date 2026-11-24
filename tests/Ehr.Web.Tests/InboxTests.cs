using Ehr.Data.Accounts;
using Ehr.Data.Inbox;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The staff inbox (the expansion plan's step 3; UC 15): helpers for its test cases, which are one file each in
// TestCases/UC15. Nothing at runtime creates an item yet, so the tests add them as the later features will, straight
// into the table. What the database refuses whatever the app does is in Ehr.Security.Tests (InboxSecurityTests).
static class InboxTests
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public static async Task<Guid> AddAsync(
        WebApps apps, Guid recipient, string summary, Guid? patient = null, string kind = InboxItemKind.Message, int minutesAgo = 0)
    {
        var id = Guid.CreateVersion7();
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ehr.inbox_items (id, recipient_id, kind, patient_id, summary, created_at)
            VALUES (@id, @recipient, @kind, @patient, @summary, now() - make_interval(mins => @ago))
            """, db);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("recipient", recipient);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("patient", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)patient ?? DBNull.Value);
        command.Parameters.AddWithValue("summary", summary);
        command.Parameters.AddWithValue("ago", minutesAgo);
        await command.ExecuteNonQueryAsync(Cancel);
        return id;
    }

    public static async Task<bool> IsDoneAsync(WebApps apps, Guid item)
    {
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT done_at IS NOT NULL FROM ehr.inbox_items WHERE id = @id", db);
        command.Parameters.AddWithValue("id", item);
        return (bool)(await command.ExecuteScalarAsync(Cancel))!;
    }

    // A clinician of the test's own, with a patient on their panel, signed in on the on-site app.
    public static async Task<(StaffAccount Clinician, Guid Patient, Session Session)> ClinicianAsync(WebApps apps, string name)
    {
        var clinician = await apps.CreateStaffAsync(name, StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Synthetic", "Inboxed");
        await apps.AssignAsync(clinician, patient);
        return (clinician, patient, await SignInAsync(apps.OnPrem, clinician));
    }

    // "Mark done", as the page's form posts it.
    public static Task<HttpResponseMessage> MarkDoneAsync(HttpClient client, Guid item, string? token = null) =>
        token is null
            ? PostFormAsync(client, $"/inbox?handler=Done&id={item}", "/inbox")
            : client.PostAsync($"/inbox?handler=Done&id={item}", Form(("__RequestVerificationToken", token)), Cancel);
}
