using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ehr.Web.Api.Sync;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The sync API (Api/Sync/QuickTextSync.cs) as the desktop app uses it. The tests in this class run one at a time,
// so each can tell which changes a pull should return.
public class QuickTextSyncTests(WebApps apps) : IClassFixture<WebApps>
{
    const string Path = "/api/sync/quick-texts";

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_first_pull_returns_every_phrase()
    {
        var id = await apps.InsertAsync(WebApps.NewShortcut(), "Already here");

        var pull = await PullAsync(apps.OnPrem, since: "0");

        Assert.Contains(pull.QuickTexts, q => q.Id == id && q.Body == "Already here");
        Assert.True(ulong.Parse(pull.Watermark, CultureInfo.InvariantCulture) > 0);
    }

    [Fact]
    public async Task A_pull_returns_only_what_changed_since_its_watermark()
    {
        var before = await PullAsync(apps.OnPrem, since: "0");
        var id = await apps.InsertAsync(WebApps.NewShortcut(), "New since");

        var next = await PullAsync(apps.OnPrem, before.Watermark);
        var after = await PullAsync(apps.OnPrem, next.Watermark);

        Assert.Equal([id], next.QuickTexts.Select(q => q.Id));
        Assert.Empty(after.QuickTexts);
    }

    // The case transaction-ID watermarks exist for. A transaction that started first commits last; a timestamp
    // bookmark would move past its change while it was still running, and skip it for good.
    [Fact]
    public async Task A_change_waits_for_every_earlier_transaction_and_is_never_skipped()
    {
        var start = await PullAsync(apps.OnPrem, since: "0");

        await using var slow = await apps.OpenDatabaseAsync();
        await using var slowTransaction = await slow.BeginTransactionAsync(Cancel);
        var slowId = await InsertAsync(slow, slowTransaction, "Started first, committed last");
        var slowTransactionId = await TransactionIdAsync(slow, slowTransaction);

        var quickId = await apps.InsertAsync(WebApps.NewShortcut(), "Started second, committed first");

        var whileRunning = await PullAsync(apps.OnPrem, start.Watermark);
        Assert.Empty(whileRunning.QuickTexts);
        Assert.True(ulong.Parse(whileRunning.Watermark, CultureInfo.InvariantCulture) <= slowTransactionId);

        await slowTransaction.CommitAsync(Cancel);
        var afterCommit = await PullAsync(apps.OnPrem, whileRunning.Watermark);

        Assert.Equal(new[] { slowId, quickId }.Order(), afterCommit.QuickTexts.Select(q => q.Id).Order());
    }

    [Fact]
    public async Task A_new_phrase_is_applied()
    {
        var operation = NewPhrase(".SYNCED", "From the desktop");

        var result = await PushOneAsync(apps.OnPrem, operation);

        Assert.Equal(PushOutcome.Applied, result.Outcome);
        Assert.Equal((".synced", 1), (result.QuickText!.Shortcut, result.QuickText.Version));
        Assert.Equal([(".synced", "From the desktop", 1)], await apps.RowsAsync(".synced"));
    }

    [Fact]
    public async Task An_edit_from_the_saved_version_is_applied()
    {
        var shortcut = WebApps.NewShortcut();
        var id = await apps.InsertAsync(shortcut, "Old", version: 4);

        var result = await PushOneAsync(apps.OnPrem, new PushOperation(Guid.CreateVersion7(), id, 4, shortcut, "New"));

        Assert.Equal(PushOutcome.Applied, result.Outcome);
        Assert.Equal([(shortcut, "New", 5)], await apps.RowsAsync(shortcut));
    }

    [Fact]
    public async Task Sending_an_operation_again_is_a_duplicate_and_changes_nothing()
    {
        var shortcut = WebApps.NewShortcut();
        var operation = NewPhrase(shortcut, "Sent twice");

        await PushOneAsync(apps.OnPrem, operation);
        var again = await PushOneAsync(apps.OnPrem, operation);
        var withinOneBatch = await PushAsync(apps.OnPrem, operation, operation);

        Assert.Equal(PushOutcome.Duplicate, again.Outcome);
        Assert.Equal(operation.Id, again.QuickText!.Id);
        Assert.All(withinOneBatch.Results, result => Assert.Equal(PushOutcome.Duplicate, result.Outcome));
        Assert.Equal([(shortcut, "Sent twice", 1)], await apps.RowsAsync(shortcut));
    }

    [Fact]
    public async Task An_edit_from_an_older_version_is_a_conflict_with_the_saved_phrase()
    {
        var shortcut = WebApps.NewShortcut();
        var id = await apps.InsertAsync(shortcut, "Saved by someone else", version: 2);

        var result = await PushOneAsync(apps.OnPrem, new PushOperation(Guid.CreateVersion7(), id, 1, shortcut, "Mine"));

        Assert.Equal(PushOutcome.Conflict, result.Outcome);
        Assert.Equal((id, "Saved by someone else", 2), (result.QuickText!.Id, result.QuickText.Body, result.QuickText.Version));
        Assert.Equal([(shortcut, "Saved by someone else", 2)], await apps.RowsAsync(shortcut));
    }

    [Fact]
    public async Task A_new_phrase_with_a_taken_shortcut_is_a_conflict_with_the_phrase_holding_it()
    {
        var shortcut = WebApps.NewShortcut();
        var holder = await apps.InsertAsync(shortcut, "Holds the shortcut");

        var result = await PushOneAsync(apps.OnPrem, NewPhrase(shortcut, "Wants it too"));

        Assert.Equal(PushOutcome.Conflict, result.Outcome);
        Assert.Equal(holder, result.QuickText!.Id);
    }

    [Fact]
    public async Task An_invalid_operation_is_refused_with_the_reason()
    {
        var result = await PushOneAsync(apps.OnPrem, NewPhrase("nad", "No period"));

        Assert.Equal(PushOutcome.Invalid, result.Outcome);
        Assert.Contains("Start with a period", Assert.Single(result.Errors!["shortcut"]));
    }

    [Fact]
    public async Task Each_operation_in_a_batch_has_its_own_outcome()
    {
        var taken = WebApps.NewShortcut();
        await apps.InsertAsync(taken, "Holds the shortcut");
        var fresh = WebApps.NewShortcut();

        var push = await PushAsync(apps.OnPrem, NewPhrase(taken, "Refused"), NewPhrase(fresh, "Applied"));

        Assert.Equal([PushOutcome.Conflict, PushOutcome.Applied], push.Results.Select(r => r.Outcome));
        Assert.Equal([(fresh, "Applied", 1)], await apps.RowsAsync(fresh));
    }

    [Fact]
    public async Task The_cloud_app_forwards_pushes_and_answers_pulls_from_its_own_database()
    {
        var start = await PullAsync(apps.Cloud, since: "0");
        var forwarded = apps.Forwarded.Count;
        var operation = NewPhrase(WebApps.NewShortcut(), "Pushed off-site");

        var result = await PushOneAsync(apps.Cloud, operation);
        var pull = await PullAsync(apps.Cloud, start.Watermark);

        Assert.Equal(PushOutcome.Applied, result.Outcome);
        Assert.Equal([operation.Id], pull.QuickTexts.Select(q => q.Id));
        Assert.Equal([$"POST {Path}"], apps.Forwarded.Skip(forwarded));
    }

    [Fact]
    public async Task A_watermark_that_isnt_one_is_refused()
    {
        using var response = await CreateClient(apps.OnPrem).GetAsync($"{Path}?since=yesterday", Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    static PushOperation NewPhrase(string shortcut, string body) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), BaseVersion: null, shortcut, body);

    static async Task<PullResponse> PullAsync(WebApplicationFactory<Program> app, string since) =>
        (await CreateClient(app).GetFromJsonAsync<PullResponse>($"{Path}?since={since}", JsonSerializerOptions.Web, Cancel))!;

    static async Task<PushResponse> PushAsync(WebApplicationFactory<Program> app, params PushOperation[] operations)
    {
        using var response = await CreateClient(app).PostAsJsonAsync(Path, new PushRequest(operations), JsonSerializerOptions.Web, Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PushResponse>(JsonSerializerOptions.Web, Cancel))!;
    }

    static async Task<PushResult> PushOneAsync(WebApplicationFactory<Program> app, PushOperation operation) =>
        Assert.Single((await PushAsync(app, operation)).Results);

    static async Task<Guid> InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string body)
    {
        var id = Guid.CreateVersion7();
        await using var command = new NpgsqlCommand(
            "INSERT INTO ehr.quick_texts (id, shortcut, body, version, updated_at) VALUES (@id, @shortcut, @body, 1, now())",
            connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("shortcut", WebApps.NewShortcut());
        command.Parameters.AddWithValue("body", body);
        await command.ExecuteNonQueryAsync(Cancel);
        return id;
    }

    static async Task<ulong> TransactionIdAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand("SELECT pg_current_xact_id()::text", connection, transaction);
        return ulong.Parse((string)(await command.ExecuteScalarAsync(Cancel))!, CultureInfo.InvariantCulture);
    }
}
