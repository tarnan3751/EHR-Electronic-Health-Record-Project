using System.Net.Http.Json;
using System.Text.Json;
using Ehr.Testing;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The lab's door (the expansion plan's step 12, UC 20): helpers for the test cases in TestCases/UC20, which call it the
// way a lab's system does, over HTTP with the key in its header. What the database refuses whatever the app does is in
// Ehr.Security.Tests (LabSecurityTests). Every test database holds the hash of MigratedDatabase.LabKey, and the on-prem
// app has the key (WebApps).
static class LabTests
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public const string Orders = "/api/lab/orders";

    public static string ResultsPath(Guid order) => $"/api/lab/orders/{order}/results";

    // A client of the app with the lab's key in its header, or another key, or none.
    public static HttpClient Client(WebApplicationFactory<Program> app, string? key = MigratedDatabase.LabKey)
    {
        var client = CreateClient(app, handleCookies: false);
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add("X-Lab-Key", key);
        }

        return client;
    }

    // Where a lab starting now would be told to ask from: every transaction before it has finished. A test asks from
    // here, so it is handed only the orders it signs.
    public static async Task<string> WatermarkAsync(WebApps apps)
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT pg_snapshot_xmin(pg_current_snapshot())::text", connection);
        return (string)(await command.ExecuteScalarAsync(Cancel))!;
    }

    // Until the lab's watermark has passed the transactions that signed the patient's lab orders. Any transaction still
    // running from before them, such as autovacuum's analyze, holds it back for a moment, as it would a real lab's; a
    // test that asks at once would be handed nothing yet.
    public static async Task SettledAsync(WebApps apps, Guid patient)
    {
        for (var tries = 0; tries < 600; tries++)
        {
            var settled = await ProxyTests.QueryAsync(apps,
                """
                SELECT coalesce(pg_snapshot_xmin(pg_current_snapshot()) > (SELECT max(l.transaction_id) FROM ehr.change_log l
                    JOIN ehr.orders o ON o.id = l.row_id WHERE l.table_name = 'orders' AND o.patient_id = @patient), true)::text
                """, patient);
            if (settled.Single() == "true")
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), Cancel);
        }
    }

    public static async Task<(HttpResponseMessage Response, JsonElement Body)> HandOverAsync(HttpClient client, string since)
    {
        var response = await client.GetAsync($"{Orders}?since={since}", Cancel);
        var text = await response.Content.ReadAsStringAsync(Cancel);
        return (response, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    // One order's results, as a lab posts them: the JSON the contract names.
    public static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid order, DateTimeOffset collectedAt, params (string Code, decimal Value, string Unit)[] results) =>
        client.PostAsJsonAsync(ResultsPath(order),
            new { collectedAt, results = results.Select(r => new { code = r.Code, value = r.Value, unit = r.Unit }) }, Cancel);

    public static Task<HttpResponseMessage> PostRawAsync(HttpClient client, Guid order, string json, string mediaType = "application/json") =>
        client.PostAsync(ResultsPath(order), new StringContent(json, System.Text.Encoding.UTF8, mediaType), Cancel);

    // The error word of a refusal, after checking the body holds exactly a word and a sentence.
    public static async Task<string> RefusalAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement;
        Assert.Equal(["error", "message"], body.EnumerateObject().Select(p => p.Name).Order());
        return body.GetProperty("error").GetString()!;
    }

    // How many results the patient has on file, and the audit events about them by source, as "action table source".
    public static Task<List<string>> ResultsOnFileAsync(WebApps apps, Guid patient) =>
        ProxyTests.QueryAsync(apps, "SELECT count(*)::text FROM ehr.observations WHERE patient_id = @patient AND category = 'laboratory'", patient);

    public static Task<List<string>> LabEventsAsync(WebApps apps, Guid patient) =>
        ProxyTests.QueryAsync(apps,
            "SELECT action || ' ' || coalesce(table_name, '-') || ' ' || coalesce(actor_id::text, '-') || ' ' || source FROM ehr.audit_events WHERE patient_id = @patient AND source = 'lab' ORDER BY occurred_at, id",
            patient);
}
