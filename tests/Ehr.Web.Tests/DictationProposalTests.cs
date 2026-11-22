using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ehr.Web.Api.Dictation;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The expansion plan's step 15 (FR-14 as reworded): the orders and allergies to add a dictation proposes, through the web
// app as each site. The service's proposals reach the review only as its listed instructions, staged once; each is opened
// in the order form or the allergy form, which fill in nothing from it, and decided there once; nothing is ordered or
// added on the chart by any of it otherwise. The stub proposes both kinds, so each test follows an order and an allergy
// (step 14's lesson: a stub that proposes nothing keeps regressions green). The forms themselves are UC 1's and UC 4's
// test cases (TestCases/UC1, TestCases/UC4).
public class DictationProposalTests(WebApps apps) : IClassFixture<WebApps>
{
    readonly DictationSidecarStub sidecar = apps.Dictation.Reset();

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // A dictation staged for review: the clinician's session and page token, the patient, the draft, the review, its page,
    // and its two proposals: the order's and the allergy's.
    internal sealed record Dictated(Session Session, string Token, Guid Patient, Guid Draft, Guid Review, string ReviewPath, Guid Order, Guid Allergy);

    // A dictation stopped, not yet staged: the clinician's session and page token, the patient, the review it is bound
    // to, its draft and the service's session.
    internal sealed record Stopped(Session Session, string Token, Guid Patient, Guid Review, Guid Draft, string SessionId);

    // A clinician dictates a new note for a patient on their panel, three sentences, and stops; the stub, unless told
    // otherwise first, then proposes an order and an allergy to add.
    internal static async Task<Stopped> StopAsync(WebApps apps, Session? session = null, Guid? patient = null)
    {
        apps.Dictation.Proposals = true;
        if (session is null)
        {
            var clinician = await apps.ClinicianAsync();
            patient = await apps.InsertPatientAsync("Synthetic", "Proposed");
            await apps.AssignAsync(clinician, patient.Value);
            session = await SignInAsync(apps.OnPrem, clinician);
        }

        var token = Token(await session.Client.GetStringAsync($"/patients/{patient}/dictate", Cancel));
        var review = Guid.NewGuid();
        using var started = await JsonPostAsync(session.Client, token, DictationApi.Path + "/for-note", new { reviewId = review, patientId = patient });
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var binding = JsonDocument.Parse(await started.Content.ReadAsStringAsync(Cancel)).RootElement;
        var sessionId = binding.GetProperty("sessionId").GetString()!;
        for (var seq = 0; seq < 3; seq++)
        {
            using var chunk = new HttpRequestMessage(HttpMethod.Post, $"{DictationApi.Path}/{sessionId}/audio?seq={seq}") { Content = new ByteArrayContent([1, 2, 3]) };
            chunk.Headers.Add("RequestVerificationToken", token);
            chunk.Content.Headers.ContentType = new("audio/webm");
            using var taken = await session.Client.SendAsync(chunk, Cancel);
            Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
        }

        using (var stopped = await JsonPostAsync(session.Client, token, $"{DictationApi.Path}/{sessionId}/stop", new { }))
        {
            Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
        }

        return new(session, token, patient!.Value, review, binding.GetProperty("draftId").GetGuid(), sessionId);
    }

    // The result as the service gives it to the clinician's browser.
    internal static async Task<JsonNode> ResultAsync(Stopped stopped)
    {
        using var result = await JsonPostAsync(stopped.Session.Client, stopped.Token, $"{DictationApi.Path}/{stopped.SessionId}/result", new { });
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        return JsonNode.Parse(await result.Content.ReadAsStringAsync(Cancel))!;
    }

    internal static Task<HttpResponseMessage> StageAsync(Stopped stopped) =>
        JsonPostAsync(stopped.Session.Client, stopped.Token, $"{DictationApi.Path}/review/{stopped.Review}/stage", new { version = 1 });

    // A dictation proposing an order and an allergy to add, staged as the dictation page stages it after Stop; with the stub
    // as it was set, when not reset.
    internal static async Task<Dictated> DictateAsync(WebApps apps, Session? session = null, Guid? patient = null, bool reset = true)
    {
        if (reset)
        {
            apps.Dictation.Reset();
        }

        var stopped = await StopAsync(apps, session, patient);
        using (var staged = await StageAsync(stopped))
        {
            Assert.Equal(HttpStatusCode.OK, staged.StatusCode);
        }

        var rows = await QueryAsync(apps, "SELECT id::text || ' ' || kind FROM ehr.ai_chart_proposals WHERE review_id = @id ORDER BY ordinal", stopped.Review);
        Assert.Equal(2, rows.Count);
        Assert.EndsWith(" order", rows[0], StringComparison.Ordinal);
        Assert.EndsWith(" allergy_addition", rows[1], StringComparison.Ordinal);
        return new(stopped.Session, stopped.Token, stopped.Patient, stopped.Draft, stopped.Review,
            $"/patients/{stopped.Patient}/notes/drafts/{stopped.Draft}/review/{stopped.Review}", Guid.Parse(rows[0][..36]), Guid.Parse(rows[1][..36]));
    }

    internal static async Task<HttpResponseMessage> JsonPostAsync(HttpClient client, string token, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("RequestVerificationToken", token);
        return await client.SendAsync(request, Cancel);
    }

    // The first column of each row, as text, for a query with one ID.
    internal static async Task<List<string>> QueryAsync(WebApps apps, string sql, Guid id)
    {
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(sql, db);
        command.Parameters.AddWithValue("id", id);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        while (await reader.ReadAsync(Cancel))
        {
            rows.Add(reader.IsDBNull(0) ? "" : reader.GetValue(0).ToString()!);
        }

        return rows;
    }

    // "state|order or -|allergy or -" of a proposal.
    internal static async Task<string> StateAsync(WebApps apps, Guid proposal) => (await QueryAsync(apps,
        "SELECT state || '|' || coalesce(order_id::text, '-') || '|' || coalesce(allergy_id::text, '-') FROM ehr.ai_chart_proposals WHERE id = @id",
        proposal)).Single();

    // How many orders, and allergies, the patient has: none from a dictation unless a clinician signed or saved them.
    internal static async Task<string> ChartAsync(WebApps apps, Guid patient) => (await QueryAsync(apps,
        "SELECT (SELECT count(*) FROM ehr.orders WHERE patient_id = @id) || ' orders, ' || (SELECT count(*) FROM ehr.allergy_intolerances WHERE patient_id = @id) || ' allergies'",
        patient)).Single();

    internal static string OrderFormPath(Guid patient, Guid proposal, string? type = null) =>
        $"/patients/{patient}/orders/new?{(type is null ? "" : $"type={type}&")}proposal={proposal}";

    internal static string AllergyFormPath(Guid patient, Guid proposal) => $"/patients/{patient}/allergies?proposal={proposal}";

    // TC-UC10-09, through the web app: what the service proposes comes back with the result as it sent it, at its listed
    // instructions' places, in no field; staging keeps each as a waiting proposal of its review, in its order, and puts
    // nothing on the chart.
    [Fact]
    public async Task TC_UC10_09_a_dictated_order_and_allergy_come_back_proposed_in_no_field_and_are_staged_waiting()
    {
        var stopped = await StopAsync(apps);
        var json = await ResultAsync(stopped);
        var text = (string)json["transcript"]!["text"]!;
        var proposals = json["proposals"]!.AsArray();
        Assert.Equal(["order", "allergy_addition"], proposals.Select(p => (string)p!["kind"]!));
        Assert.Equal([DictationSidecarStub.OrderSaid, DictationSidecarStub.AllergySaid], proposals.Select(p => text[(int)p!["start"]!..(int)p!["end"]!]));
        Assert.Equal(proposals.Select(p => $"{p!["start"]}:{p["end"]}"), json["notCarriedOver"]!.AsArray().Select(i => $"{i!["start"]}:{i["end"]}"));
        Assert.Equal(["end", "kind", "start", "warnings"], proposals[0]!.AsObject().Select(property => property.Key).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("lipid", json["fields"]!.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("penicillin", json["fields"]!.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        using (var staged = await StageAsync(stopped))
        {
            Assert.Equal(HttpStatusCode.OK, staged.StatusCode);
        }

        Assert.Equal(["0 order pending", "1 allergy_addition pending"], await QueryAsync(apps,
            "SELECT ordinal || ' ' || kind || ' ' || state FROM ehr.ai_chart_proposals WHERE review_id = @id ORDER BY ordinal", stopped.Review));
        Assert.Equal(["order allergy_addition"], await QueryAsync(apps,
            "SELECT string_agg(p ->> 'kind', ' ') FROM ehr.ai_note_reviews r, jsonb_array_elements(r.result_json -> 'proposals') p WHERE r.id = @id", stopped.Review));
        Assert.Equal("0 orders, 0 allergies", await ChartAsync(apps, stopped.Patient));
    }

    // TC-UC10-11: a result that proposes anything but its listed instructions, each of its kind, once, from a final
    // transcript, with a proposal's warnings, at most twenty, is never staged: the service's answer is refused, and neither
    // a proposal nor a field is kept. A kind the contract doesn't know fails the read itself.
    [Theory]
    [InlineData("not-listed")]
    [InlineData("other-kind")]
    [InlineData("twice")]
    [InlineData("too-many")]
    [InlineData("field-warning")]
    [InlineData("warning-range")]
    [InlineData("not-final")]
    [InlineData("unknown-kind")]
    public async Task TC_UC10_11_a_proposal_that_isnt_one_of_the_listed_instructions_is_never_staged(string fault)
    {
        var stopped = await StopAsync(apps);
        var json = await ResultAsync(stopped);
        var proposals = json["proposals"]!.AsArray();
        var listed = json["notCarriedOver"]!.AsArray();
        switch (fault)
        {
            case "not-listed":
                proposals[0]!["start"] = (int)proposals[0]!["start"]! + 1;
                break;
            case "other-kind":
                proposals[0]!["kind"] = "allergy_addition";
                break;
            case "twice":
                proposals.Add(proposals[0]!.DeepClone());
                break;
            case "too-many":
                // Twenty-one listed instructions, each proposed once: one more than a dictation may propose.
                listed.Clear();
                proposals.Clear();
                for (var i = 0; i < 21; i++)
                {
                    listed.Add(new JsonObject { ["reason"] = "order", ["start"] = i, ["end"] = i + 1 });
                    proposals.Add(new JsonObject { ["kind"] = "order", ["start"] = i, ["end"] = i + 1, ["warnings"] = new JsonArray() });
                }

                break;
            case "field-warning":
                proposals[0]!["warnings"]!.AsArray().Add(new JsonObject { ["code"] = "one_field_holds_dictation", ["start"] = 0, ["end"] = 5 });
                break;
            case "warning-range":
                proposals[1]!["warnings"]![0]!["end"] = 1_000_000;
                break;
            case "not-final":
                // Speech recognition failed on the rest of the recording: the transcript is as it was live, not final.
                json["sorting"] = "unavailable";
                json["reason"] = "transcription_failed";
                json["fields"] = null;
                json["transcript"]!["final"] = false;
                break;
            default:
                proposals[0]!["kind"] = "discontinuation";
                break;
        }

        sidecar.Answer = (HttpStatusCode.OK, json.ToJsonString());
        using var stage = await StageAsync(stopped);
        Assert.Equal(HttpStatusCode.BadGateway, stage.StatusCode);
        Assert.Equal(["0 0 recording"], await QueryAsync(apps,
            """
            SELECT (SELECT count(*) FROM ehr.ai_chart_proposals WHERE review_id = @id) || ' ' || (SELECT count(*) FROM ehr.ai_field_proposals WHERE review_id = @id)
                || ' ' || state FROM ehr.ai_note_reviews WHERE id = @id
            """, stopped.Review));
    }

    // "Sign order" for a lab test on the order form opened from the dictation's order proposal, as the form posts it
    // without JavaScript, with any reasons it asks for: the page it ends on.
    internal static async Task<HttpResponseMessage> UC1OrderAsync(Dictated dictated, string item, string type = "lab",
        (string Name, string Value)[]? details = null, string? duplicateReason = null, string? safetyReason = null, HttpClient? client = null)
    {
        client ??= dictated.Session.Client;
        var path = OrderFormPath(dictated.Patient, dictated.Order, type);
        var token = Token(await client.GetStringAsync(path, Cancel));
        (string, object)[] reasons =
        [
            .. duplicateReason is null ? [] : new[] { ("DuplicateReason", (object)duplicateReason) },
            .. safetyReason is null ? [] : new[] { ("SafetyReason", (object)safetyReason) },
        ];
        return await client.PostAsync(path, Form([("__RequestVerificationToken", token), ("Item", item), ("SafetySeen", "true"),
            .. (details ?? OrderTests.Cbc).Select(d => ($"Details[{d.Name}]", (object)d.Value)), .. reasons]), Cancel);
    }

    // Two tabs signing, or saving, one proposal at once, each having read it waiting: the database holds the second behind
    // the first on the patient's row, finds the proposal decided when its turn comes, and its form goes back to its
    // savepoint, so it leaves no order or allergy behind and says so. The test holds the patient's row itself until both
    // are waiting there, so the two run in that order every time.
    [Theory]
    [InlineData("order")]
    [InlineData("allergy")]
    public async Task Two_tabs_deciding_one_proposal_at_once_leave_one_decision_and_nothing_else(string kind)
    {
        var dictated = await DictateAsync(apps);
        var (path, proposal, table) = kind == "order"
            ? (OrderFormPath(dictated.Patient, dictated.Order, "lab"), dictated.Order, "orders")
            : (AllergyFormPath(dictated.Patient, dictated.Allergy), dictated.Allergy, "allergy_intolerances");
        (string Name, object Value)[][] posts = kind == "order"
            ? [[("Item", "Lipid panel"), ("Details[indication]", "Synthetic fatigue")], [("Item", "Hemoglobin A1c"), ("Details[indication]", "Synthetic fatigue")]]
            : [[("Name", "Penicillin G"), ("Status", "active")], [("Name", "Amoxicillin"), ("Status", "active")]];
        var tokens = new[] { Token(await dictated.Session.Client.GetStringAsync(path, Cancel)), Token(await dictated.Session.Client.GetStringAsync(path, Cancel)) };

        await using var holding = await apps.OpenDatabaseAsync();
        await using var held = await holding.BeginTransactionAsync(Cancel);
        await using (var hold = new NpgsqlCommand("SELECT 1 FROM ehr.patients WHERE id = @id FOR NO KEY UPDATE", holding, held))
        {
            hold.Parameters.AddWithValue("id", dictated.Patient);
            await hold.ExecuteNonQueryAsync(Cancel);
        }

        var sent = tokens.Select((token, tab) => dictated.Session.Client.PostAsync(path,
            Form([("__RequestVerificationToken", token), ("SafetySeen", "true"), .. posts[tab]]), Cancel)).ToArray();
        // Watched from a connection of its own: a transaction sees the server's activity as it was when it first looked.
        await using var watching = await apps.OpenDatabaseAsync();
        for (var tries = 0; ; tries++)
        {
            await using var count = new NpgsqlCommand(
                $"SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE 'INSERT INTO ehr.{table}%'", watching);
            if ((long)(await count.ExecuteScalarAsync(Cancel))! >= 2)
            {
                break;
            }

            Assert.True(tries < 200, "The two tabs never both waited for the patient's row.");
            await Task.Delay(50, Cancel);
        }

        await held.CommitAsync(Cancel);
        var responses = await Task.WhenAll(sent);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        var refused = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Contains("Not saved: this proposal was decided first, in another tab or window.", RefillRequestTests.Said(await refused.Content.ReadAsStringAsync(Cancel)));
        var written = await QueryAsync(apps, $"SELECT id FROM ehr.{table} WHERE patient_id = @id", dictated.Patient);
        Assert.Single(written);
        Assert.Equal(kind == "order" ? $"ordered|{written[0]}|-" : $"recorded|-|{written[0]}", await StateAsync(apps, proposal));
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }
}
