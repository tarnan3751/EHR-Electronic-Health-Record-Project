using System.Net;
using System.Text.Json;
using Ehr.Testing;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Step 19's ceremonies as passkeys.js runs them, against an app, with a TestAuthenticator in place of the device: the
// first post's JSON answer, then the passkey sent back as a plain form.
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
}
