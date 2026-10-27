using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Xunit;

namespace Ehr.Web.Tests;

// Uses the apps the way a browser does: over HTTPS, keeping cookies in a jar, following redirects, and posting forms
// with the token the page rendered. A session is signed in through one app and can be pointed at another with the
// same jar, since both apps share the keys that protect the cookies.
static partial class Browser
{
    public static readonly Uri Base = new("https://localhost");

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // An anonymous client with no jar of its own, for requests that bring their own Cookie header.
    public static HttpClient CreateClient(WebApplicationFactory<Program> app, bool handleCookies = true) =>
        app.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = Base,
            HandleCookies = handleCookies,
        });

    public static HttpClient Client(WebApplicationFactory<Program> app, CookieContainer jar, bool followRedirects = true) =>
        followRedirects
            ? app.CreateDefaultClient(Base, new RedirectHandler(), new CookieContainerHandler(jar))
            : app.CreateDefaultClient(Base, new CookieContainerHandler(jar));

    // Signs in as a staff account, password then authenticator code, through `via` (or `app`), and returns a session
    // against `app` with the resulting cookies.
    public static async Task<Session> SignInAsync(WebApplicationFactory<Program> app, StaffAccount account, WebApplicationFactory<Program>? via = null)
    {
        var jar = new CookieContainer();
        var client = Client(via ?? app, jar);

        var codePage = await PostFormAsync(client, "/sign-in", "/sign-in", ("Input.Email", account.Email), ("Input.Password", account.Password));
        await ExpectAsync(codePage, "/sign-in/code");
        // The home sends each account to its first page: a clinician's panel, an admin's staff page.
        using var home = await PostFormAsync(client, "/sign-in/code", codePage, ("Code", Totp.Code(account.AuthenticatorKey)));
        if (home.StatusCode != HttpStatusCode.OK || PathOf(home).StartsWith("/sign-in", StringComparison.Ordinal))
        {
            Assert.Fail($"Expected to be signed in, got {(int)home.StatusCode} at {PathOf(home)}:\n{await home.Content.ReadAsStringAsync(Cancel)}");
        }

        return new Session(jar, via is null || via == app ? client : Client(app, jar));
    }

    public const string QuickTexts = "/quick-texts";

    // The quick text page, signed in as the fixture's clinician.
    public static async Task<Page> OpenAsync(WebApps apps, WebApplicationFactory<Program> app, WebApplicationFactory<Program>? via = null)
    {
        var session = await SignInAsync(app, await apps.ClinicianAsync(), via);
        var html = await session.Client.GetStringAsync(QuickTexts, Cancel);
        return new Page(session.Client, Token(html));
    }

    // The Cookie header a signed-in browser would send, for requests built by hand.
    public static async Task<string> CookieHeaderAsync(WebApplicationFactory<Program> app, StaffAccount account)
    {
        var session = await SignInAsync(app, account);
        return session.Jar.GetCookieHeader(Base);
    }

    // GETs the page at `formPath` (or reuses the response that already shows it), takes its token, and posts the form.
    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string formPath, string pagePath, params (string Name, object Value)[] fields)
    {
        using var page = await client.GetAsync(pagePath, Cancel);
        return await PostFormAsync(client, formPath, page, fields);
    }

    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string formPath, HttpResponseMessage page, params (string Name, object Value)[] fields)
    {
        var token = Token(await page.Content.ReadAsStringAsync(Cancel));
        return await client.PostAsync(formPath, Form([.. fields, ("__RequestVerificationToken", token)]), Cancel);
    }

    public static FormUrlEncodedContent Form(params (string Name, object Value)[] fields) =>
        new(fields.Select(field => KeyValuePair.Create(field.Name, field.Value.ToString())));

    public static async Task<string> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Cancel);
    }

    // Where a followed request ended up.
    public static string PathOf(HttpResponseMessage response) => response.RequestMessage!.RequestUri!.AbsolutePath;

    public static string Token(string html)
    {
        var match = FormToken().Match(html);
        Assert.True(match.Success, "The page has no form token.");
        return match.Groups[1].Value;
    }

    static async Task ExpectAsync(HttpResponseMessage response, string path)
    {
        if (response.StatusCode != HttpStatusCode.OK || PathOf(response) != path)
        {
            Assert.Fail($"Expected to land on {path} with 200, got {(int)response.StatusCode} at {PathOf(response)}:\n{await response.Content.ReadAsStringAsync(Cancel)}");
        }
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex FormToken();
}

// A signed-in browser: the jar with its cookies, and a client against one app that follows redirects.
sealed record Session(CookieContainer Jar, HttpClient Client);

sealed record Page(HttpClient Client, string Token)
{
    public Task<HttpResponseMessage> PostAsync(string handler, params (string Name, object Value)[] fields) =>
        Client.PostAsync($"{Browser.QuickTexts}?handler={handler}", Browser.Form([.. fields, ("__RequestVerificationToken", Token)]),
            TestContext.Current.CancellationToken);
}
