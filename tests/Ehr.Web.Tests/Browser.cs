using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Ehr.Web.Tests;

// Uses a page the way a browser does: over HTTPS, keeping cookies, with the form token the page rendered.
static partial class Browser
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public static HttpClient CreateClient(WebApplicationFactory<Program> app, bool handleCookies = true) =>
        app.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = handleCookies,
        });

    public static async Task<Page> OpenAsync(WebApplicationFactory<Program> app)
    {
        var client = CreateClient(app);
        var html = await client.GetStringAsync("/", Cancel);
        return new Page(client, FormToken().Match(html).Groups[1].Value);
    }

    public static FormUrlEncodedContent Form(params (string Name, object Value)[] fields) =>
        new(fields.Select(field => KeyValuePair.Create(field.Name, field.Value.ToString())));

    public static async Task<string> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Cancel);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex FormToken();
}

sealed record Page(HttpClient Client, string Token)
{
    public Task<HttpResponseMessage> PostAsync(string handler, params (string Name, object Value)[] fields) =>
        Client.PostAsync($"/?handler={handler}", Browser.Form([.. fields, ("__RequestVerificationToken", Token)]),
            TestContext.Current.CancellationToken);
}
