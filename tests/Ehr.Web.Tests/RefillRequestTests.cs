using System.Net;
using System.Text.RegularExpressions;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Refill requests (the expansion plan's step 13d): helpers for the test cases TC-UC6-51 to 58, TC-UC21-01 to 06,
// TC-UC15-08 and TC-UC1-22. What the database refuses whatever the app does is in Ehr.Security.Tests
// (RefillRequestSecurityTests).
static partial class RefillRequestTests
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public const string PortalPath = "/portal/refills";

    // Lisinopril 10 mg, as the chart's coded entry names it (RxNorm 314076); the clinic's common item for it is
    // "Lisinopril 10 mg tablet" (SYN-MED-001).
    public const string Lisinopril = "lisinopril 10 MG Oral Tablet";

    public const string LisinoprilCode = "314076";

    // A medication on the patient's chart, inserted as the superuser the way a seed would: its ID.
    public static async Task<Guid> MedicationAsync(WebApps apps, Guid patient, string display = Lisinopril, string? code = LisinoprilCode, string status = "active",
        bool sensitive = false, string dosage = "1 tablet by mouth daily")
    {
        var id = Guid.CreateVersion7();
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO ehr.medication_statements (id, patient_id, code, display, dosage, status, sensitive) VALUES (@id, @patient, @code, @display, @dosage, @status, @sensitive)",
            db);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("patient", patient);
        command.Parameters.AddWithValue("code", (object?)code ?? DBNull.Value);
        command.Parameters.AddWithValue("display", display);
        command.Parameters.AddWithValue("dosage", dosage);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("sensitive", sensitive);
        await command.ExecuteNonQueryAsync(Cancel);
        return id;
    }

    // The reference the portal's page gives a medication, as a browser posts it when that one is chosen.
    public static string ReferenceOf(string html, string display)
    {
        var match = Regex.Match(html, "name=\"Medication\" value=\"([^\"]+)\"[^>]*>\\s*<span>" + Regex.Escape(WebUtility.HtmlEncode(display)));
        Assert.True(match.Success, $"The page offers no {display}.");
        return match.Groups[1].Value;
    }

    // "Send request" for the medication of that name, as the page posts it, with sensitive items asked for or not: the
    // page it ends on.
    public static async Task<HttpResponseMessage> AskAsync(HttpClient client, string display, string? note = null, bool sensitive = false)
    {
        var path = PortalPath + (sensitive ? "?sensitive=true" : "");
        var html = await client.GetStringAsync(path, Cancel);
        return await client.PostAsync(path, Form(("Medication", ReferenceOf(html, display)), ("Note", note ?? ""), ("__RequestVerificationToken", Token(html))), Cancel);
    }

    // A patient's requests as the database keeps them, oldest first: "status|medication|note|asked by|ordered|message|answered by".
    public static Task<List<string>> RequestsAsync(WebApps apps, Guid patient) => ProxyTests.QueryAsync(apps,
        """
        SELECT status || '|' || medication_display || '|' || coalesce(note, '-') || '|' || requested_by || '|' || coalesce(ordered_display, '-') || '|'
            || coalesce(decline_message, '-') || '|' || coalesce(decided_by::text, '-')
        FROM ehr.refill_requests WHERE patient_id = @patient ORDER BY requested_at
        """, patient);

    // The patient's latest request: its ID.
    public static async Task<Guid> RequestIdAsync(WebApps apps, Guid patient) =>
        Guid.Parse((await ProxyTests.QueryAsync(apps, "SELECT id::text FROM ehr.refill_requests WHERE patient_id = @patient ORDER BY requested_at DESC LIMIT 1", patient)).Single());

    // Whether markup carries an ID of the clinic's: a UUID in either of its forms.
    public static bool HasAnId(string html) => Uuid().IsMatch(html);

    [GeneratedRegex("[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}")]
    private static partial Regex Uuid();
}
