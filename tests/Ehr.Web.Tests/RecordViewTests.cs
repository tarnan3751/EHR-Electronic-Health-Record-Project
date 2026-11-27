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
    // signing in, which lists the panel, is no view of this patient: each test's lines are exactly the views it makes. On
    // the panel by this machine's clock, as views are recorded, so a proxy without sensitive items is shown them.
    public static async Task<(StaffAccount Clinician, Session Session, ProxyTests.Person Patient)> ViewedPatientAsync(WebApps apps, string family)
    {
        var clinician = await apps.ClinicianAsync();
        var person = await ProxyTests.PersonAsync(apps, "Synthetic", family);
        var session = await SignInAsync(apps.OnPrem, clinician);
        await AssignNowAsync(apps, clinician, person.Patient);
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

    // The part each line by someone of this kind ("you", "staff", "proxy", "lab") names, in the page's order.
    public static List<string> PartsOf(string html, string kind) =>
        [.. Lines(html).Where(line => line.StartsWith(kind + " | ", StringComparison.Ordinal)).Select(line => line.Split(" | ")[2])];

    // Reads of a patient's record by an account, as the PHI reader records them with their part (step 13c2): each
    // (action, table, part), a second apart, from ten seconds into the minute that began the given minutes ago, so they
    // fall in one minute whenever the test runs. None for a part writes a read as before that step.
    public static async Task PartReadsAsync(WebApps apps, Guid patient, Guid actor, int minutesAgo, params (string Action, string Table, string? Part)[] reads)
    {
        await using var connection = await apps.OpenDatabaseAsync();
        for (var i = 0; i < reads.Length; i++)
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source, sensitive, part)
                VALUES (uuidv7(), date_trunc('minute', now()) - make_interval(mins => @minutes) + make_interval(secs => 10 + @second), @actor, @action, @table,
                        CASE WHEN @table = 'audit_events' THEN NULL ELSE gen_random_uuid() END, @patient, 'onprem', false, @part)
                """, connection);
            command.Parameters.AddWithValue("minutes", minutesAgo);
            command.Parameters.AddWithValue("second", i);
            command.Parameters.AddWithValue("actor", actor);
            command.Parameters.AddWithValue("action", reads[i].Action);
            command.Parameters.AddWithValue("table", reads[i].Table);
            command.Parameters.AddWithValue("patient", patient);
            command.Parameters.AddWithValue("part", (object?)reads[i].Part ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(Cancel);
        }
    }

    // A clinician's day with the patient on it, an appointment a minute from now, and an item about the patient in their
    // inbox: booked and sent as the clinician.
    public static async Task DayAndInboxAsync(WebApps apps, Guid clinician, Guid patient)
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var transaction = await connection.BeginTransactionAsync(Cancel);
        await using (var context = new NpgsqlCommand("SELECT set_config('app.user_id', @user, true)", connection, transaction))
        {
            context.Parameters.AddWithValue("user", clinician.ToString());
            await context.ExecuteNonQueryAsync(Cancel);
        }

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ehr.appointments (id, patient_id, clinician_id, starts_at, minutes, reason, status)
            VALUES (gen_random_uuid(), @patient, @clinician, now() + interval '1 minute', 15, 'Synthetic follow-up', 'booked');
            INSERT INTO ehr.inbox_items (id, recipient_id, kind, patient_id, summary, created_at)
            VALUES (gen_random_uuid(), @clinician, 'message', @patient, 'Synthetic message', now());
            """, connection, transaction);
        command.Parameters.AddWithValue("patient", patient);
        command.Parameters.AddWithValue("clinician", clinician);
        await command.ExecuteNonQueryAsync(Cancel);
        await transaction.CommitAsync(Cancel);
    }

    // A part's words without how many times, for a test that doesn't care how a minute's views were cut.
    public static string Uncounted(string part) => Regex.Replace(part, @", \d+ times$", "");

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
