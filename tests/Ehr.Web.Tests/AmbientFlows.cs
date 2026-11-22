using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ehr.Web.Api.Dictation;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Listening to a visit (the expansion plan's step 16; UC 23) through the clinician API (Api/Dictation/AmbientApi.cs) and
// its review, for UC 23's test cases (TestCases/UC23), against the stub's sessions that listen
// (DictationSidecarStub.Ambient.cs). All data is synthetic.
static class AmbientFlows
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public const string ListenApi = DictationApi.AmbientPath;

    public sealed record Listening(Session Session, Guid Patient, Guid Review, string SessionId, string Token, long Version);

    // A visit drafted and staged for review: where its review is, its draft, and the draft's version now.
    public sealed record Drafted(Listening Listening, string ReviewUrl, Guid Draft, long Version);

    public static async Task<(Session Session, Guid Patient, string Token)> OpenAsync(WebApps apps)
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Synthetic", "Listening");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(apps.OnPrem, clinician);
        var token = Token(await session.Client.GetStringAsync($"/patients/{patient}/listen", Cancel));
        return (session, patient, token);
    }

    public static object Consent(Guid review, Guid patient, int speakers = 2, string givenBy = "patient", bool agreed = true, string? language = null) =>
        new { reviewId = review, patientId = patient, speakers, givenBy, agreed, spokenLanguage = language };

    public static async Task<Listening> StartAsync(WebApps apps, int speakers = 2)
    {
        var (session, patient, token) = await OpenAsync(apps);
        var review = Guid.NewGuid();
        using var response = await Post(session.Client, token, $"{ListenApi}/start", Consent(review, patient, speakers));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await Json(response);
        Assert.Equal("recording", json.GetProperty("state").GetString());
        return new(session, patient, review, json.GetProperty("sessionId").GetString()!, token, json.GetProperty("version").GetInt64());
    }

    // The whole visit to its review: a chunk of audio, Stop, every voice named (1 the clinician, 2 the patient, 3 someone
    // else), the draft, and the review staged as the page stages it.
    public static async Task<Drafted> DraftAsync(WebApps apps, int speakers = 2)
    {
        var l = await StartAsync(apps, speakers);
        using (var chunk = await Audio(l, 0))
        {
            Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        }

        using (var stop = await Post(l.Session.Client, l.Token, $"{ListenApi}/{l.SessionId}/stop"))
        {
            Assert.Equal("awaiting_speakers", (await Json(stop)).GetProperty("sorting").GetString());
        }

        var roles = new Dictionary<string, string> { ["1"] = "clinician", ["2"] = "patient" };
        if (speakers == 3)
        {
            roles["3"] = "other";
        }

        using (var named = await Post(l.Session.Client, l.Token, $"{ListenApi}/{l.SessionId}/speakers", new { speakers = roles }))
        {
            Assert.Equal(HttpStatusCode.OK, named.StatusCode);
        }

        using (var result = await Post(l.Session.Client, l.Token, $"{ListenApi}/{l.SessionId}/result"))
        {
            Assert.Equal("done", (await Json(result)).GetProperty("sorting").GetString());
        }

        using var staged = await Post(l.Session.Client, l.Token, $"{DictationApi.Path}/review/{l.Review}/stage", new { version = l.Version });
        Assert.Equal(HttpStatusCode.OK, staged.StatusCode);
        var json = await Json(staged);
        var url = json.GetProperty("url").GetString()!;
        return new(l, url, Guid.Parse(url.Split('/')[^3]), json.GetProperty("version").GetInt64());
    }

    public static async Task<HttpResponseMessage> Post(HttpClient client, string token, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("RequestVerificationToken", token);
        return await client.SendAsync(request, Cancel);
    }

    public static async Task<HttpResponseMessage> Audio(Listening l, int seq, string? path = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{path ?? ListenApi}/{l.SessionId}/audio?seq={seq}");
        request.Headers.Add("RequestVerificationToken", l.Token);
        request.Content = new ByteArrayContent([1, 2, 3]);
        request.Content.Headers.ContentType = new("audio/webm");
        return await l.Session.Client.SendAsync(request, Cancel);
    }

    // A form post on the review page, as its script sends one: answered in JSON.
    public static async Task<HttpResponseMessage> ReviewPost(HttpClient client, string token, string url, string handler, params (string Name, object Value)[] fields)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url + "?handler=" + handler)
        {
            Content = Form([("__RequestVerificationToken", token), .. fields]),
        };
        request.Headers.Add("HX-Request", "true");
        return await client.SendAsync(request, Cancel);
    }

    public static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.Clone();

    // The first column of each row, as text, read as the database's superuser.
    public static async Task<List<string>> RowsAsync(WebApps apps, string sql, Guid id)
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        var rows = new List<string>();
        while (await reader.ReadAsync(Cancel)) rows.Add(reader.GetValue(0).ToString()!);
        return rows;
    }

    // A review left open past its transcript's time: the transcript aged 8 days, as the database's owner ages a row with
    // the triggers held, then the purge as the on-site app's timer runs it, keeping 7 days. Answers how many it deleted.
    public static async Task<long> PurgeAsync(WebApps apps, Guid review)
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using (var transaction = await connection.BeginTransactionAsync(Cancel))
        await using (var aged = new NpgsqlCommand(
            "SET LOCAL session_replication_role = replica; UPDATE ehr.ambient_transcripts SET created_at = created_at - interval '8 days' WHERE id = @id",
            connection, transaction))
        {
            aged.Parameters.AddWithValue("id", review);
            await aged.ExecuteNonQueryAsync(Cancel);
            await transaction.CommitAsync(Cancel);
        }

        await using var purge = new NpgsqlCommand("SELECT deleted FROM ehr.purge_ambient_transcripts(interval '7 days')", connection);
        return (long)(await purge.ExecuteScalarAsync(Cancel))!;
    }

    // The review's proposals, by field: id and state.
    public static async Task<Dictionary<string, (Guid Id, string State)>> ProposalsAsync(WebApps apps, Guid review)
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT field, id, state FROM ehr.ai_field_proposals WHERE review_id = @id", connection);
        command.Parameters.AddWithValue("id", review);
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        var rows = new Dictionary<string, (Guid, string)>();
        while (await reader.ReadAsync(Cancel)) rows[reader.GetString(0)] = (reader.GetGuid(1), reader.GetString(2));
        return rows;
    }

    public static Task<HttpResponseMessage> Decide(HttpClient client, string token, string url, Guid proposal, long version, string decision, string edited = "") =>
        ReviewPost(client, token, url, "Decide", ("ProposalId", proposal), ("Version", version), ("DecisionKey", Guid.NewGuid()),
            ("Decision", decision), ("EditedValue", edited));
}
