using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The expansion plan's step 13c, "Who viewed my record": helpers for its test cases in TestCases/UC6. What the database
// lets each account learn from the audit log is in Ehr.Security.Tests (RecordViewSecurityTests).
static partial class RecordViewTests
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public const string ViewsPath = "/portal/views";

    // The page's lines, in its order, each as "kind | who | part | when": the kind is its data-who, the rest its words.
    public static List<string> Lines(string html) =>
        [.. Line().Matches(html).Select(line => line.Groups["kind"].Value + " | " + string.Join(" | ",
            Part().Matches(line.Groups["body"].Value).Select(part => Regex.Replace(Words(part.Groups["words"].Value), @"\s+", " ").Trim())))];

    [GeneratedRegex("<li data-who=\"(?<kind>[a-z]+)\">(?<body>.*?)</li>", RegexOptions.Singleline)]
    private static partial Regex Line();

    [GeneratedRegex("<div[^>]*>(?<words>.*?)</div>", RegexOptions.Singleline)]
    private static partial Regex Part();

    // The links to the next pages: where "Newer views" and "Older views" go, when the page has them.
    public static (string? Newer, string? Older) Links(string html) => (LinkTo(html, "Newer"), LinkTo(html, "Older"));

    static string? LinkTo(string html, string which) =>
        Link().Matches(html).FirstOrDefault(link => link.Groups["which"].Value == which) is { } found
            ? System.Net.WebUtility.HtmlDecode(found.Groups["href"].Value)
            : null;

    [GeneratedRegex("<a [^>]*href=\"(?<href>[^\"]+)\"[^>]*>(?<which>Newer|Older) views</a>")]
    private static partial Regex Link();

    // A moment as the page writes it: "Nov 2, 2026, 14:05 UTC".
    public static string Shown(DateTimeOffset moment) => moment.UtcDateTime.ToString("MMM d, yyyy, HH:mm", CultureInfo.InvariantCulture) + " UTC";

    // A patient signed in to the portal, and a clinician who signed in before putting them on their panel, so that their
    // signing in, which lists the panel, is no view of this patient: each test's lines are exactly the views it makes.
    public static async Task<(StaffAccount Clinician, Session Session, ProxyTests.Person Patient)> ViewedPatientAsync(WebApps apps, string family)
    {
        var clinician = await apps.ClinicianAsync();
        var person = await ProxyTests.PersonAsync(apps, "Synthetic", family);
        var session = await SignInAsync(apps.OnPrem, clinician);
        await apps.AssignAsync(clinician, person.Patient);
        return (clinician, session, person);
    }

    // The patient on the clinician's panel from this moment by this machine's clock, the one the app records views by,
    // rather than the database's, which in a container may run a little ahead.
    public static async Task AssignNowAsync(WebApps apps, StaffAccount clinician, Guid patient)
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO ehr.patient_assignments (id, patient_id, clinician_id, assigned_at) VALUES (gen_random_uuid(), @patient, @clinician, @at)", connection);
        command.Parameters.AddWithValue("patient", patient);
        command.Parameters.AddWithValue("clinician", clinician.Id);
        command.Parameters.AddWithValue("at", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(Cancel);
    }

    // The page, as the client's browser gets it.
    public static Task<string> PageAsync(HttpClient client, string query = "") => client.GetStringAsync(ViewsPath + query, Cancel);

    // Views of a patient's record by an account, written to the log as the PHI reader writes a read of the patient, a
    // minute apart, the newest the given minutes ago: for the test cases that need more than the pages can make quickly.
    public static async Task ReadsAsync(WebApps apps, Guid patient, Guid actor, int count, int newestMinutesAgo = 1, string table = "patients", string action = "read")
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source, sensitive)
            SELECT uuidv7(), now() - make_interval(mins => @newest + n), @actor, @action, @table, @patient, @patient, 'onprem', false
            FROM generate_series(0, @count - 1) AS n
            """, connection);
        command.Parameters.AddWithValue("newest", newestMinutesAgo);
        command.Parameters.AddWithValue("actor", actor);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("patient", patient);
        command.Parameters.AddWithValue("count", count);
        await command.ExecuteNonQueryAsync(Cancel);
    }

    // When the latest event of the patient's by that account and of that table was recorded.
    public static async Task<DateTimeOffset> LatestAsync(WebApps apps, Guid patient, Guid actor, string table = "patients")
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "SELECT max(occurred_at) FROM ehr.audit_events WHERE patient_id = @patient AND actor_id = @actor AND table_name = @table", connection);
        command.Parameters.AddWithValue("patient", patient);
        command.Parameters.AddWithValue("actor", actor);
        command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        Assert.True(await reader.ReadAsync(Cancel));
        return reader.GetFieldValue<DateTimeOffset>(0);
    }
}
