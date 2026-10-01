using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Web.Tests;

// The quick text page at /, on each server, through the same handlers htmx calls. The tests share one database,
// so each one uses its own shortcuts.
public partial class QuickTextPageTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Home_page_lists_phrases_with_their_text_encoded()
    {
        var shortcut = NewShortcut();
        await InsertAsync(shortcut, "<script>alert(1)</script>");

        var html = await apps.OnPrem.CreateClient().GetStringAsync("/", Cancel);

        Assert.Contains(shortcut, html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>alert(1)", html);
    }

    [Fact]
    public async Task Adding_saves_the_phrase_in_lowercase_and_returns_its_row()
    {
        var shortcut = NewShortcut();
        var page = await OpenAsync(apps.OnPrem);

        using var response = await page.PostAsync("Add", ("Shortcut", shortcut.ToUpperInvariant()), ("Body", "No acute distress."));

        var html = await ReadAsync(response);
        Assert.Contains("""<ul hx-swap-oob="beforeend:#quick-texts">""", html);
        Assert.Contains("No acute distress.", html);
        Assert.Equal([(shortcut, "No acute distress.", 1)], await RowsAsync(shortcut));
    }

    [Fact]
    public async Task Adding_a_shortcut_already_in_use_is_refused()
    {
        var shortcut = NewShortcut();
        await InsertAsync(shortcut, "First");
        var page = await OpenAsync(apps.OnPrem);

        using var response = await page.PostAsync("Add", ("Shortcut", shortcut), ("Body", "Second"));

        Assert.Contains($"{shortcut} is already in use.", await ReadAsync(response));
        Assert.Equal([(shortcut, "First", 1)], await RowsAsync(shortcut));
    }

    [Theory]
    [InlineData("nad", "Text", "Start with a period")]
    [InlineData(".two words", "Text", "Start with a period")]
    [InlineData(".abcdefghijklmnopqrstuvwxyz1234567", "Text", "Use 32 characters or fewer.")]
    [InlineData(".no-text", " ", "Enter the text.")]
    public async Task Invalid_input_is_refused(string shortcut, string body, string message)
    {
        var page = await OpenAsync(apps.OnPrem);

        using var response = await page.PostAsync("Add", ("Shortcut", shortcut), ("Body", body));

        Assert.Contains(message, await ReadAsync(response));
        Assert.Empty(await RowsAsync(shortcut.ToLowerInvariant()));
    }

    [Fact]
    public async Task Edit_starts_from_the_saved_version()
    {
        var id = await InsertAsync(NewShortcut(), "Text", version: 3);
        var client = apps.OnPrem.CreateClient();

        var html = await client.GetStringAsync($"/?handler=Edit&id={id}", Cancel);

        Assert.Contains("name=\"Version\" value=\"3\"", html);
        Assert.Contains("__RequestVerificationToken", html);
    }

    [Fact]
    public async Task Saving_from_the_saved_version_updates_the_phrase()
    {
        var shortcut = NewShortcut();
        var id = await InsertAsync(shortcut, "Old text");
        var page = await OpenAsync(apps.OnPrem);

        using var response = await page.PostAsync("Save", ("Id", id), ("Version", 1), ("Shortcut", shortcut), ("Body", "New text"));

        Assert.Contains("New text", await ReadAsync(response));
        Assert.Equal([(shortcut, "New text", 2)], await RowsAsync(shortcut));
    }

    [Fact]
    public async Task Saving_from_an_older_version_saves_nothing_and_shows_both_versions()
    {
        var shortcut = NewShortcut();
        var id = await InsertAsync(shortcut, "Saved by someone else", version: 2);
        var page = await OpenAsync(apps.OnPrem);

        using var response = await page.PostAsync("Save", ("Id", id), ("Version", 1), ("Shortcut", shortcut), ("Body", "My edit"));

        var html = await ReadAsync(response);
        Assert.Contains("Saved by someone else", html);
        Assert.Contains("My edit", html);
        // Keep mine posts from their version, so it can save over it.
        Assert.Contains("name=\"Version\" value=\"2\"", html);
        Assert.Equal([(shortcut, "Saved by someone else", 2)], await RowsAsync(shortcut));
    }

    [Fact]
    public async Task Saves_without_the_form_token_are_refused()
    {
        var shortcut = NewShortcut();
        var client = apps.OnPrem.CreateClient();
        await client.GetStringAsync("/", Cancel);

        using var response = await client.PostAsync("/?handler=Add", Form(("Shortcut", shortcut), ("Body", "Text")), Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await RowsAsync(shortcut));
    }

    [Fact]
    public async Task Form_token_keys_are_stored_in_the_database()
    {
        await OpenAsync(apps.OnPrem);

        await using var connection = await OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM ehr.data_protection_keys", connection);
        Assert.True((long)(await command.ExecuteScalarAsync(Cancel))! > 0);
    }

    [Fact]
    public async Task Cloud_shows_phrases_read_only()
    {
        await InsertAsync(NewShortcut(), "Read from the cloud");

        var html = await apps.Cloud.CreateClient().GetStringAsync("/", Cancel);

        Assert.Contains("Read from the cloud", html);
        Assert.Contains("can't be saved off-site yet", html);
        Assert.DoesNotContain("<form", html);
        Assert.DoesNotContain("hx-get", html);
    }

    [Fact]
    public async Task Cloud_refuses_saves()
    {
        var shortcut = NewShortcut();

        using var response = await apps.Cloud.CreateClient()
            .PostAsync("/?handler=Add", Form(("Shortcut", shortcut), ("Body", "Text")), Cancel);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Empty(await RowsAsync(shortcut));
    }

    static string NewShortcut() => "." + Guid.NewGuid().ToString("N")[..12];

    // Opens the page as a browser does: the client keeps the form-token cookie, and the token comes from the page.
    static async Task<Page> OpenAsync(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        var html = await client.GetStringAsync("/", Cancel);
        return new Page(client, FormToken().Match(html).Groups[1].Value);
    }

    sealed record Page(HttpClient Client, string Token)
    {
        public Task<HttpResponseMessage> PostAsync(string handler, params (string Name, object Value)[] fields) =>
            Client.PostAsync($"/?handler={handler}", Form([.. fields, ("__RequestVerificationToken", Token)]), Cancel);
    }

    static FormUrlEncodedContent Form(params (string Name, object Value)[] fields) =>
        new(fields.Select(field => KeyValuePair.Create(field.Name, field.Value.ToString())));

    static async Task<string> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Cancel);
    }

    async Task<Guid> InsertAsync(string shortcut, string body, int version = 1)
    {
        var id = Guid.CreateVersion7();
        await using var connection = await OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO ehr.quick_texts (id, shortcut, body, version, updated_at) VALUES (@id, @shortcut, @body, @version, now())",
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("shortcut", shortcut);
        command.Parameters.AddWithValue("body", body);
        command.Parameters.AddWithValue("version", version);
        await command.ExecuteNonQueryAsync(Cancel);
        return id;
    }

    async Task<List<(string Shortcut, string Body, int Version)>> RowsAsync(string shortcut)
    {
        await using var connection = await OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "SELECT shortcut, body, version FROM ehr.quick_texts WHERE shortcut = @shortcut", connection);
        command.Parameters.AddWithValue("shortcut", shortcut);
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        var rows = new List<(string, string, int)>();
        while (await reader.ReadAsync(Cancel))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        }

        return rows;
    }

    // As the superuser, to set up and inspect rows around what the app does.
    async Task<NpgsqlConnection> OpenDatabaseAsync()
    {
        var connection = new NpgsqlConnection(apps.Database.SuperuserConnectionString);
        await connection.OpenAsync(Cancel);
        return connection;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex FormToken();
}
