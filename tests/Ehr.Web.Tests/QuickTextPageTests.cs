using System.Net;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The quick text page at /, on each server, through the same handlers htmx calls. The tests share one database,
// so each one uses its own shortcuts.
public class QuickTextPageTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Home_page_lists_phrases_with_their_text_encoded()
    {
        var shortcut = WebApps.NewShortcut();
        await apps.InsertAsync(shortcut, "<script>alert(1)</script>");

        var html = await CreateClient(apps.OnPrem).GetStringAsync("/", Cancel);

        Assert.Contains(shortcut, html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>alert(1)", html);
    }

    [Fact]
    public async Task Adding_saves_the_phrase_in_lowercase_and_returns_its_row()
    {
        var shortcut = WebApps.NewShortcut();
        var page = await OpenAsync(apps.OnPrem);

        using var response = await page.PostAsync("Add", ("Shortcut", shortcut.ToUpperInvariant()), ("Body", "No acute distress."));

        var html = await ReadAsync(response);
        Assert.Contains("""<ul hx-swap-oob="beforeend:#quick-texts">""", html);
        Assert.Contains("No acute distress.", html);
        Assert.Equal([(shortcut, "No acute distress.", 1)], await apps.RowsAsync(shortcut));
    }

    [Fact]
    public async Task Adding_a_shortcut_already_in_use_is_refused()
    {
        var shortcut = WebApps.NewShortcut();
        await apps.InsertAsync(shortcut, "First");
        var page = await OpenAsync(apps.OnPrem);

        using var response = await page.PostAsync("Add", ("Shortcut", shortcut), ("Body", "Second"));

        Assert.Contains($"{shortcut} is already in use.", await ReadAsync(response));
        Assert.Equal([(shortcut, "First", 1)], await apps.RowsAsync(shortcut));
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
        Assert.Empty(await apps.RowsAsync(shortcut.ToLowerInvariant()));
    }

    [Fact]
    public async Task Edit_starts_from_the_saved_version()
    {
        var id = await apps.InsertAsync(WebApps.NewShortcut(), "Text", version: 3);
        var client = CreateClient(apps.OnPrem);

        var html = await client.GetStringAsync($"/?handler=Edit&id={id}", Cancel);

        Assert.Contains("name=\"Version\" value=\"3\"", html);
        Assert.Contains("__RequestVerificationToken", html);
    }

    [Fact]
    public async Task Saving_from_the_saved_version_updates_the_phrase()
    {
        var shortcut = WebApps.NewShortcut();
        var id = await apps.InsertAsync(shortcut, "Old text");
        var page = await OpenAsync(apps.OnPrem);

        using var response = await page.PostAsync("Save", ("Id", id), ("Version", 1), ("Shortcut", shortcut), ("Body", "New text"));

        Assert.Contains("New text", await ReadAsync(response));
        Assert.Equal([(shortcut, "New text", 2)], await apps.RowsAsync(shortcut));
    }

    [Fact]
    public async Task Saving_from_an_older_version_saves_nothing_and_shows_both_versions()
    {
        var shortcut = WebApps.NewShortcut();
        var id = await apps.InsertAsync(shortcut, "Saved by someone else", version: 2);
        var page = await OpenAsync(apps.OnPrem);

        using var response = await page.PostAsync("Save", ("Id", id), ("Version", 1), ("Shortcut", shortcut), ("Body", "My edit"));

        var html = await ReadAsync(response);
        Assert.Contains("Saved by someone else", html);
        Assert.Contains("My edit", html);
        // Keep mine posts from their version, so it can save over it.
        Assert.Contains("name=\"Version\" value=\"2\"", html);
        Assert.Equal([(shortcut, "Saved by someone else", 2)], await apps.RowsAsync(shortcut));
    }

    [Fact]
    public async Task Saves_without_the_form_token_are_refused()
    {
        var shortcut = WebApps.NewShortcut();
        var client = CreateClient(apps.OnPrem);
        await client.GetStringAsync("/", Cancel);

        using var response = await client.PostAsync("/?handler=Add", Form(("Shortcut", shortcut), ("Body", "Text")), Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await apps.RowsAsync(shortcut));
    }

    [Fact]
    public async Task Form_token_keys_are_stored_in_the_database()
    {
        await OpenAsync(apps.OnPrem);

        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM ehr.data_protection_keys", connection);
        Assert.True((long)(await command.ExecuteScalarAsync(Cancel))! > 0);
    }

    [Fact]
    public async Task Cloud_app_shows_phrases_from_its_database_with_its_forms()
    {
        await apps.InsertAsync(WebApps.NewShortcut(), "Read from the cloud");

        var html = await CreateClient(apps.Cloud).GetStringAsync("/", Cancel);

        Assert.Contains("Read from the cloud", html);
        Assert.Contains("id=\"add-quick-text\"", html);
        Assert.Contains("__RequestVerificationToken", html);
    }
}
