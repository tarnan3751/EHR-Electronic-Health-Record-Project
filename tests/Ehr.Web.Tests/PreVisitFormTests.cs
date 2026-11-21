using System.Globalization;
using System.Text.Json;
using Ehr.Testing;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Pre-visit forms (the expansion plan's step 13e): helpers for the test cases in TestCases/UC22. What the database
// refuses whatever the app does is in Ehr.Security.Tests (PreVisitFormSecurityTests).
static class PreVisitFormTests
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public const string Phq9 = "44249-1";

    public const string Gad7 = "69737-5";

    // The answer codes of both questionnaires' scored questions, by score (LOINC LL358-3).
    public static readonly string[] Frequency = ["LA6568-5", "LA6569-3", "LA6570-1", "LA6571-9"];

    public static readonly string[] PhqItems = ["44250-9", "44255-8", "44259-0", "44254-1", "44251-7", "44258-2", "44252-5", "44253-3", "44260-8"];

    public static readonly string[] GadItems = ["69725-0", "68509-9", "69733-4", "69734-2", "69735-9", "69689-8", "69736-7"];

    // Each question's answer, by the score at its place.
    public static Dictionary<string, string> Answers(string[] items, params int[] scores) =>
        items.Select((item, place) => (item, Frequency[scores[place]])).ToDictionary(pair => pair.item, pair => pair.Item2);

    // A clinician signed in on the on-site app, a patient on their panel with a confirmed portal account, and an
    // appointment with the clinician `days` from now, booked through the page.
    public sealed record Visit(StaffAccount Clinician, Session Session, Guid Patient, Guid Account, string Email, Guid Appointment);

    public static async Task<Visit> VisitAsync(WebApps apps, string name, int days = 2, string time = "16:00")
    {
        var (clinician, patient, session) = await AppointmentTests.ClinicianAsync(apps, name);
        var (email, _) = await apps.CreatePatientAccountAsync(patient);
        var account = Guid.Parse((await QueryAsync(apps, "SELECT id::text FROM ehr.patient_users WHERE patient_id = @patient", ("patient", patient))).Single());
        var appointment = await BookAsync(apps, session, patient, clinician.Id, days, time);
        return new Visit(clinician, session, patient, account, email, appointment);
    }

    // Another appointment for the patient, through the booking page; its ID.
    public static async Task<Guid> BookAsync(WebApps apps, Session session, Guid patient, Guid clinician, int days, string time)
    {
        var day = DateTime.UtcNow.Date.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var before = await AppointmentTests.RowsAsync(apps, patient, "id::text");
        using var booked = await AppointmentTests.BookAsync(session.Client, patient, clinician, day, time, "Synthetic: follow-up");
        Assert.Equal(System.Net.HttpStatusCode.OK, booked.StatusCode);
        return Guid.Parse((await AppointmentTests.RowsAsync(apps, patient, "id::text")).Except(before).Single());
    }

    public static string FormsPath(Guid patient) => $"/patients/{patient}/forms";

    // "Ask the patient to fill it in", as the forms page posts it.
    public static Task<HttpResponseMessage> AskAsync(HttpClient client, Guid patient, Guid appointment, string questionnaire) =>
        PostFormAsync(client, FormsPath(patient) + "?handler=Ask", FormsPath(patient), ("AppointmentId", appointment), ("Questionnaire", questionnaire));

    // The ID of the form asked for at an appointment.
    public static async Task<Guid> RequestAsync(WebApps apps, Guid appointment, string questionnaire) =>
        Guid.Parse((await QueryAsync(apps, "SELECT id::text FROM ehr.questionnaire_requests WHERE appointment_id = @appointment AND questionnaire_code = @code",
            ("appointment", appointment), ("code", questionnaire))).Single());

    // The answers sent by a portal account (under a proxy grant, for a proxy), as the portal's session does, as the
    // app's role, straight to the database: the clinician's test cases don't go through the portal's page.
    public static async Task<Guid> AnswerAsync(WebApps apps, Guid account, Guid request, Dictionary<string, string> answers, Guid? grant = null)
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(apps.Database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword));
        await connection.OpenAsync(Cancel);
        await using var transaction = await connection.BeginTransactionAsync(Cancel);
        await using (var set = new NpgsqlCommand("SELECT set_config('app.user_id', @user, true), set_config('app.acting_grant', @grant, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("user", account.ToString());
            set.Parameters.AddWithValue("grant", grant?.ToString() ?? "");
            await set.ExecuteNonQueryAsync(Cancel);
        }

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO ehr.questionnaire_responses (id, request_id, patient_id, questionnaire_code, clinician_id, answers) VALUES (@id, @request, gen_random_uuid(), '', gen_random_uuid(), @answers::jsonb)",
            connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("request", request);
            insert.Parameters.AddWithValue("answers", JsonSerializer.Serialize(answers));
            await insert.ExecuteNonQueryAsync(Cancel);
        }

        await transaction.CommitAsync(Cancel);
        return id;
    }

    public static string FormPath(Guid request) => $"/portal/forms/{request}";

    // The patient's own portal session, signed in on the on-site app.
    public static async Task<(HttpClient Client, System.Net.CookieContainer Jar)> PortalAsync(WebApps apps, Visit visit)
    {
        var (client, jar, path, _) = await ProxyTests.SignInAsync(apps, visit.Email);
        Assert.Equal("/portal", path);
        return (client, jar);
    }

    // "Send to my care team", as the form posts it: each question's answer, by its code. The token comes from the
    // Access page, which every portal session has, so a form the session can't see is posted to just the same.
    public static Task<HttpResponseMessage> SendAsync(HttpClient client, Guid request, Dictionary<string, string> answers, params (string Name, object Value)[] more) =>
        PostFormAsync(client, FormPath(request), "/portal/access", [.. answers.Select(answer => ($"Answers[{answer.Key}]", (object)answer.Value)), .. more]);

    // A page's words with each run of white space made one space, so a table's row reads as one line.
    public static string Text(string html) => System.Text.RegularExpressions.Regex.Replace(Words(html), "\\s+", " ");

    // A proxy grant the patient gives in the portal's database rules, as their own account: to `email`, for 30 days,
    // with or without sensitive items. Its ID.
    public static async Task<Guid> GrantAsync(WebApps apps, Guid patientAccount, Guid patient, string email, bool sensitive)
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(apps.Database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword));
        await connection.OpenAsync(Cancel);
        await using var transaction = await connection.BeginTransactionAsync(Cancel);
        await using (var set = new NpgsqlCommand("SELECT set_config('app.user_id', @user, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("user", patientAccount.ToString());
            await set.ExecuteNonQueryAsync(Cancel);
        }

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO ehr.proxy_grants (id, patient_id, proxy_email, scope, include_sensitive, expires_at, granted_by, granted_at, granted_by_kind)
            VALUES (@id, @patient, @email, 'record', @sensitive, now() + interval '30 days', gen_random_uuid(), now(), 'patient')
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("patient", patient);
            insert.Parameters.AddWithValue("email", email);
            insert.Parameters.AddWithValue("sensitive", sensitive);
            await insert.ExecuteNonQueryAsync(Cancel);
        }

        await transaction.CommitAsync(Cancel);
        return id;
    }

    // The first column of each row, as text, read as the superuser.
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
}
