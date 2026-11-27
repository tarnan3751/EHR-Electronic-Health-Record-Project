using System.Net;
using System.Text.Json;
using Ehr.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Playwright;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Step 19's ceremonies as passkeys.js runs them, against an app, with a TestAuthenticator in place of the device: the
// first post's JSON answer, then the passkey sent back as a plain form. Adding one on the passkeys page, and using one
// on the code page after the password. And, to put requests racing on one account in an order, the account's row held
// from outside the apps (step 19.1).
static class PasskeyFlows
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public const string PasskeysPage = "/passkeys";

    // The add form's first post, as the script sends it: the name and a code.
    public static Task<HttpResponseMessage> OptionsToAddAsync(HttpClient client, string name, string code) =>
        PostFormAsync(client, $"{PasskeysPage}?handler=Options", PasskeysPage, ("name", name), ("code", code));

    // The options and the state from a first post that was accepted.
    public static async Task<(JsonElement Options, string State)> ReadyAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answer = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(Cancel));
        return (answer.GetProperty("options"), answer.GetProperty("state").GetString()!);
    }

    // The passkey the authenticator made, sent back with the state.
    public static Task<HttpResponseMessage> FinishAddAsync(HttpClient client, string credential, string state) =>
        PostFormAsync(client, $"{PasskeysPage}?handler=Add", PasskeysPage, ("credential", credential), ("state", state));

    // A passkey added to the signed-in account at the origin given, its page after.
    public static async Task<HttpResponseMessage> AddAsync(
        HttpClient client, StaffAccount account, TestAuthenticator authenticator, string name = "This Mac", string origin = WebApps.OnSiteOrigin,
        bool verified = true)
    {
        using var first = await OptionsToAddAsync(client, name, Totp.Code(account.AuthenticatorKey));
        var (options, state) = await ReadyAsync(first);
        return await FinishAddAsync(client, authenticator.Create(options, origin, verified), state);
    }

    public const string CodePage = "/sign-in/code";

    // A browser that has given the account's password, on the code page.
    public static async Task<(HttpClient Client, string CodePage)> PasswordAsync(WebApplicationFactory<Program> app, StaffAccount account, CookieContainer? jar = null)
    {
        var client = Client(app, jar ?? new CookieContainer());
        using var codePage = await PostFormAsync(client, "/sign-in", "/sign-in", ("Input.Email", account.Email), ("Input.Password", account.Password));
        Assert.Equal(CodePage, PathOf(codePage));
        return (client, await codePage.Content.ReadAsStringAsync(Cancel));
    }

    // The code page's first post for a passkey, as the script sends it.
    public static Task<HttpResponseMessage> OptionsToSignInAsync(HttpClient client, string codePage) =>
        client.PostAsync($"{CodePage}?handler=Options", Form(("__RequestVerificationToken", Token(codePage))), Cancel);

    // The passkey the authenticator used, sent back with the state.
    public static Task<HttpResponseMessage> PasskeyAsync(HttpClient client, string codePage, string credential, string state) =>
        client.PostAsync($"{CodePage}?handler=Passkey", Form(("credential", credential), ("state", state), ("__RequestVerificationToken", Token(codePage))), Cancel);

    // Signs in through `app` with the password and the passkey, from the origin given; the page it lands on.
    public static async Task<HttpResponseMessage> SignInWithPasskeyAsync(
        WebApplicationFactory<Program> app, StaffAccount account, TestAuthenticator authenticator, string origin = WebApps.OnSiteOrigin, CookieContainer? jar = null)
    {
        var (client, codePage) = await PasswordAsync(app, account, jar);
        using var first = await OptionsToSignInAsync(client, codePage);
        var (options, state) = await ReadyAsync(first);
        return await PasskeyAsync(client, codePage, authenticator.Get(options, origin), state);
    }

    // Chromium's own virtual authenticator for the page, as a device with a PIN or biometric that verifies the person;
    // the DevTools session it was added in and its ID, to change it with.
    public static async Task<(ICDPSession DevTools, string Id)> VirtualAuthenticatorAsync(IBrowserContext context, IPage page)
    {
        var devTools = await context.NewCDPSessionAsync(page);
        await devTools.SendAsync("WebAuthn.enable");
        var added = await devTools.SendAsync("WebAuthn.addVirtualAuthenticator", new Dictionary<string, object>
        {
            ["options"] = new Dictionary<string, object>
            {
                ["protocol"] = "ctap2",
                ["transport"] = "internal",
                ["hasResidentKey"] = true,
                ["hasUserVerification"] = true,
                ["isUserVerified"] = true,
                ["automaticPresenceSimulation"] = true,
            },
        });
        return (devTools, added!.Value.GetProperty("authenticatorId").GetString()!);
    }

    // The password typed on the sign-in page, from the keyboard; on to the code page. Typing waits for the page to put the
    // focus in its email field itself (autofocus): Chromium does that at its next rendering after the page has loaded, so
    // on a slow machine keys pressed as soon as the page has loaded can land before it, and the first characters of the
    // email are lost (CI, run 45718664981).
    public static async Task PasswordInBrowserAsync(IPage page, string url, StaffAccount account)
    {
        await page.GotoAsync($"{url}/sign-in");
        await page.WaitForFunctionAsync("() => document.activeElement?.id === 'Input_Email'");
        await page.Keyboard.TypeAsync(account.Email);
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync(account.Password);
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync($"**{CodePage}*");
    }

    // The current code typed on a code page that offers no passkey, once the page has put the focus in its field itself;
    // on to the page the sign-in lands on.
    public static async Task CodeInBrowserAsync(IPage page, StaffAccount account)
    {
        await page.WaitForFunctionAsync("() => document.activeElement?.id === 'Code'");
        await page.Keyboard.TypeAsync(Totp.Code(account.AuthenticatorKey));
        await page.Keyboard.PressAsync("Enter");
    }

    // A passkey added on the passkeys page from the keyboard: the name, the current code, Enter.
    public static async Task AddInBrowserAsync(IPage page, string url, StaffAccount account, string name)
    {
        await page.GotoAsync($"{url}{PasskeysPage}");
        await page.WaitForSelectorAsync("[data-passkey-add]:not([hidden])");
        await page.FocusAsync("#passkey-name");
        await page.Keyboard.TypeAsync(name);
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync(Totp.Code(account.AuthenticatorKey));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForSelectorAsync("text=Passkey added. Use it at your next sign-in");
    }

    // The on-site app as Chromium reaches it through a ClinicalBrowserHost: passkeys tied to localhost, made and used only
    // at the host's own address. The caller disposes it.
    public static WebApplicationFactory<Program> AppAt(WebApps apps, string url) =>
        apps.OnPrem.WithWebHostBuilder(builder => builder
            .UseSetting("Ehr:Passkeys:Domain", "localhost")
            .UseSetting("Ehr:Passkeys:Origins:0", url)
            .UseSetting("Ehr:Passkeys:Origins:1", url));

    // A staff account's row held from outside the apps, as a request's StaffUserLock holds it, to put requests for the
    // account in an order (step 19.1): each that holds the account waits, in turn, until this transaction ends.
    public static async Task<AccountHold> HoldAccountAsync(WebApps apps, Guid account)
    {
        var connection = await apps.OpenDatabaseAsync();
        var transaction = await connection.BeginTransactionAsync(Cancel);
        await using var command = new NpgsqlCommand("SELECT 1 FROM ehr.staff_users WHERE id = @id FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("id", account);
        await command.ExecuteScalarAsync(Cancel);
        return new(connection, transaction);
    }

    // Until this many requests are waiting for a lock. Each test class has a database of its own, and runs one test
    // at a time.
    public static async Task WaitingForLocksAsync(WebApps apps, int requests)
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock'", connection);
        for (var tries = 0; tries < 400; tries++)
        {
            if ((long)(await command.ExecuteScalarAsync(Cancel))! >= requests)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), Cancel);
        }

        Assert.Fail($"{requests} requests never waited for a lock.");
    }
}

// The transaction holding an account's row (PasskeyFlows.HoldAccountAsync), with its connection; disposed, it ends.
sealed class AccountHold(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
{
    public NpgsqlConnection Connection => connection;

    public NpgsqlTransaction Transaction => transaction;

    public async ValueTask DisposeAsync()
    {
        await transaction.DisposeAsync();
        await connection.DisposeAsync();
    }
}
