using System.Net;
using System.Text.Json;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.PasskeyFlows;

namespace Ehr.Web.Tests.TestCases.UC11;

// Test Case ID: TC-UC11-18
// Purpose / scenario: A passkey is a second factor only when it is the account's own, used by the person the device
//   verified, at a staff site's own address, for this password step (the expansion plan's step 19). Anything else is
//   refused the way a wrong code is: counted towards the lockout, recorded, and the code page as it was, so the code
//   still signs in. Five refusals lock the account, and a locked account's good passkey is refused too.
// Type: Functional (invalid input)
// Requirements: proposed FR-67, FR-28
// Use case: UC 11, Sign in with multi-factor authentication
// Preconditions: Dr. Wrong Passkey and Dr. Other Passkey are active with an authenticator app, each with a passkey
//   added on-site, Dr. Wrong Passkey's used once to sign in. The browser has given Dr. Wrong Passkey's password.
// Inputs and system conditions: POST /sign-in/code?handler=Options, then ?handler=Passkey with each wrong passkey, each
//   after its own password step; then five in one password step, then a good one.
// Test data: Software authenticators (TestAuthenticator), each answer wrong in one way: the person not verified; made at
//   the portal's address, or in a cross-origin frame; for another relying party (ehr.example.test); a count no higher
//   than the last; the other account's passkey; a state from an earlier password step; no state.
// Steps:
//   1. For each wrong answer, give the password, then the passkey.
//   2. Give the password, then the code.
//   3. Without a password step, ask for a passkey's options.
//   4. Give the password, then five unverified passkeys, then the good one, then the code.
// Expected result: Step 1: each stays on /sign-in/code with "That passkey didn't work. Try again, or enter the code from
//   your authenticator app." and the code's field, and is a sign_in_failed, "passkey refused". Step 2: signed in.
//   Step 3: 400, "Not signed in. This sign-in took too long. Start again with your password.", and no options. Step 4:
//   after the fifth the account is locked; the good passkey is refused with "This account is locked after too many
//   failed attempts. Try again in 15 minutes." and a sign_in_failed, "locked out"; no sign_in.
// Creator: Tahseen Arnan
public class TC_UC11_18_PasskeyRefused(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    const string Refused = "That passkey didn't work. Try again, or enter the code from your authenticator app.";

    [Fact]
    public async Task Each_wrong_passkey_is_refused_like_a_wrong_code_and_the_code_still_signs_in()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Wrong Passkey", StaffRoles.Clinician);
        var other = await apps.CreateStaffAsync("Dr. Other Passkey", StaffRoles.Clinician);
        using var authenticator = new TestAuthenticator();
        using var othersAuthenticator = new TestAuthenticator();
        await AddPasskeyAsync(clinician, authenticator);
        await AddPasskeyAsync(other, othersAuthenticator);
        using (var first = await SignInWithPasskeyAsync(apps.OnPrem, clinician, authenticator))
        {
            Assert.Equal("/patients", PathOf(first));
        }

        // An earlier password step's state, for the last but one.
        var (earlierClient, earlierPage) = await PasswordAsync(apps.OnPrem, clinician);
        using var earlierOptions = await OptionsToSignInAsync(earlierClient, earlierPage);
        var (_, earlierState) = await ReadyAsync(earlierOptions);

        var wrong = new Func<JsonElement, string, (string Credential, string State)>[]
        {
            (options, state) => (authenticator.Get(options, WebApps.OnSiteOrigin, verified: false), state),
            (options, state) => (authenticator.Get(options, "https://portal.example.test"), state),
            (options, state) => (authenticator.Get(options, WebApps.OnSiteOrigin, crossOrigin: true), state),
            (options, state) => (authenticator.Get(options, WebApps.OnSiteOrigin, relyingParty: "ehr.example.test"), state),
            // The count the first sign-in left, again.
            (options, state) => (authenticator.Get(options, WebApps.OnSiteOrigin, signCount: 1), state),
            (options, state) => (othersAuthenticator.Get(options, WebApps.OnSiteOrigin), state),
            (options, _) => (authenticator.Get(options, WebApps.OnSiteOrigin), earlierState),
            (options, _) => (authenticator.Get(options, WebApps.OnSiteOrigin), ""),
        };

        // 1.
        foreach (var answer in wrong)
        {
            var (client, codePage) = await PasswordAsync(apps.OnPrem, clinician);
            using var optionsResponse = await OptionsToSignInAsync(client, codePage);
            var (options, state) = await ReadyAsync(optionsResponse);
            var (credential, sent) = answer(options, state);

            using var refused = await PasskeyAsync(client, codePage, credential, sent);

            Assert.Equal(CodePage, PathOf(refused));
            var page = await ReadAsync(refused);
            Assert.Contains(Refused, Words(page));
            Assert.Contains("id=\"Code\"", page);
        }

        Assert.Equal(wrong.Length, await ReasonsAsync(clinician, "passkey refused"));
        // The one that added the passkey, with the code, and the first with the passkey; none since.
        Assert.Equal(2, await apps.AuditEventsAsync(AuditActions.SignIn, actorId: clinician.Id));

        // 2.
        await SignInAsync(apps.OnPrem, clinician);

        // 3.
        var fresh = Client(apps.OnPrem, new CookieContainer());
        var signInPage = await fresh.GetStringAsync("/sign-in", Cancel);
        using var noPasswordStep = await fresh.PostAsync($"{CodePage}?handler=Options", Form(("__RequestVerificationToken", Token(signInPage))), Cancel);
        Assert.Equal(HttpStatusCode.BadRequest, noPasswordStep.StatusCode);
        var answerWithout = JsonSerializer.Deserialize<JsonElement>(await noPasswordStep.Content.ReadAsStringAsync(Cancel));
        Assert.Equal("Not signed in. This sign-in took too long. Start again with your password.", answerWithout.GetProperty("problem").GetString());
        Assert.False(answerWithout.TryGetProperty("options", out _));
    }

    [Fact]
    public async Task Five_refused_passkeys_lock_the_account_and_then_a_good_one_is_refused()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Locked Passkey", StaffRoles.Clinician);
        using var authenticator = new TestAuthenticator();
        await AddPasskeyAsync(clinician, authenticator);

        // 4.
        var (client, codePage) = await PasswordAsync(apps.OnPrem, clinician);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var optionsResponse = await OptionsToSignInAsync(client, codePage);
            var (options, state) = await ReadyAsync(optionsResponse);
            using var refused = await PasskeyAsync(client, codePage, authenticator.Get(options, WebApps.OnSiteOrigin, verified: false), state);
            Assert.Contains(Refused, Words(await ReadAsync(refused)));
        }

        using var lastOptions = await OptionsToSignInAsync(client, codePage);
        var (good, goodState) = await ReadyAsync(lastOptions);
        using var locked = await PasskeyAsync(client, codePage, authenticator.Get(good, WebApps.OnSiteOrigin), goodState);

        Assert.Equal(CodePage, PathOf(locked));
        Assert.Contains("This account is locked after too many failed attempts. Try again in 15 minutes.", Words(await ReadAsync(locked)));
        Assert.Equal(1, await ReasonsAsync(clinician, "locked out"));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.SignIn, actorId: clinician.Id));
        await using var scope = apps.OnPrem.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        Assert.True(await users.IsLockedOutAsync((await users.FindByIdAsync(clinician.Id.ToString()))!));
    }

    // The passkey added on-site, through the page, signed in with the code.
    async Task AddPasskeyAsync(StaffAccount account, TestAuthenticator authenticator)
    {
        using var added = await AddAsync((await SignInAsync(apps.OnPrem, account)).Client, account, authenticator);
        Assert.Contains("Passkey added.", Words(await ReadAsync(added)));
    }

    // How many of the account's failed sign-ins were for this reason.
    async Task<long> ReasonsAsync(StaffAccount account, string reason)
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM ehr.audit_events WHERE action = @action AND actor_id = @actor AND reason = @reason", connection);
        command.Parameters.AddWithValue("action", AuditActions.SignInFailed);
        command.Parameters.AddWithValue("actor", account.Id);
        command.Parameters.AddWithValue("reason", reason);
        return (long)(await command.ExecuteScalarAsync(Cancel))!;
    }
}
