using System.Buffers.Text;
using System.Net;
using System.Text.Json;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Domain.Accounts;
using Ehr.Testing;
using Ehr.Web.CloudAudit;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.PasskeyFlows;

namespace Ehr.Web.Tests;

// A staff member's own passkeys at /passkeys (step 19, StaffPasskeys), checked against the server with a software
// authenticator: added only with a current authenticator code, never a recovery code, a wrong one counted towards the
// lockout; only a passkey that verified the person, made at a staff site's own address; the state bound to the account
// and to the sign-in; a name and at most ten; removed only by its owner, or all of them by an administrator's
// authenticator reset, which no add finishing at the same time or begun before it outlives (step 19.1); recorded without
// its name; and off-site, both posts forwarded to the clinic's server, or, cut off from it, not offered.
public class PasskeyPageTests(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_passkey_is_added_with_a_current_code_listed_and_removed_by_its_owner()
    {
        var account = await apps.CreateStaffAsync("Dr. Adds A Passkey", StaffRoles.Clinician);
        var session = await SignInAsync(apps.OnPrem, account);
        using var authenticator = new TestAuthenticator();

        using var added = await AddAsync(session.Client, account, authenticator, name: "This Mac");

        var page = Words(await ReadAsync(added));
        Assert.Contains("Passkey added.", page);
        Assert.Contains("This Mac", page);
        Assert.Contains("Lost a device? Ask an administrator to reset your authenticator.", page);
        var stored = Assert.Single(await PasskeysAsync(account));
        Assert.Equal(authenticator.CredentialId, stored.CredentialId);
        Assert.True(stored.IsUserVerified);
        var reference = StaffPasskeys.Reference(authenticator.CredentialId);
        Assert.Equal(new (string?, string?)[] { (null, $$"""{"passkey": "{{reference}}"}""") }, await EventsAsync(AuditActions.PasskeyAdded, account));

        using var removed = await PostFormAsync(session.Client, $"{PasskeysPage}?handler=Remove", PasskeysPage, ("id", Base64Url.EncodeToString(authenticator.CredentialId)));

        Assert.Contains("Passkey removed.", Words(await ReadAsync(removed)));
        Assert.Empty(await PasskeysAsync(account));
        Assert.Equal(new (string?, string?)[] { (null, $$"""{"passkey": "{{reference}}"}""") }, await EventsAsync(AuditActions.PasskeyRemoved, account));
        Assert.DoesNotContain(await AllDetailsAsync(account), details => details?.Contains("This Mac", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task A_wrong_code_or_a_recovery_code_is_refused_and_counted()
    {
        var account = await apps.CreateStaffAsync("Dr. Wrong Code Passkey", StaffRoles.Clinician);
        var session = await SignInAsync(apps.OnPrem, account);
        string recoveryCode;
        await using (var scope = apps.OnPrem.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
            recoveryCode = (await users.GenerateNewTwoFactorRecoveryCodesAsync((await users.FindByIdAsync(account.Id.ToString()))!, 1))!.Single();
        }

        foreach (var code in new[] { Totp.WrongCode(account.AuthenticatorKey), recoveryCode })
        {
            using var refused = await OptionsToAddAsync(session.Client, "This Mac", code);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            var answer = JsonSerializer.Deserialize<JsonElement>(await refused.Content.ReadAsStringAsync(Cancel));
            Assert.Equal("Code", answer.GetProperty("field").GetString());
            Assert.Equal("Not saved. Wrong code.", answer.GetProperty("problem").GetString());
            Assert.False(answer.TryGetProperty("options", out _));
        }

        Assert.Equal(2, (await AccountAsync(account)).AccessFailedCount);
        Assert.Equal(new (string?, string?)[] { ("wrong code", null), ("wrong code", null) }, await EventsAsync(AuditActions.PasskeyRefused, account));
        Assert.Empty(await PasskeysAsync(account));
    }

    [Fact]
    public async Task A_passkey_that_did_not_verify_the_person_or_was_made_elsewhere_is_not_added()
    {
        var account = await apps.CreateStaffAsync("Dr. Unverified Passkey", StaffRoles.Clinician);
        var session = await SignInAsync(apps.OnPrem, account);

        foreach (var (verified, origin) in new[] { (false, WebApps.OnSiteOrigin), (true, "https://portal.example.test"), (true, "http://ehr.example.test") })
        {
            using var authenticator = new TestAuthenticator();
            using var refused = await AddAsync(session.Client, account, authenticator, origin: origin, verified: verified);

            Assert.Contains("Not saved. The passkey must ask for a PIN, fingerprint or face.", Words(await ReadAsync(refused)));
        }

        Assert.Empty(await PasskeysAsync(account));
        Assert.Equal(3, (await EventsAsync(AuditActions.PasskeyRefused, account)).Count(e => e.Reason == "not accepted"));
        // Not a guess at a code: the person had given one.
        Assert.Equal(0, (await AccountAsync(account)).AccessFailedCount);
    }

    [Fact]
    public async Task A_state_from_another_account_or_another_sign_in_is_refused()
    {
        var account = await apps.CreateStaffAsync("Dr. First Sign In", StaffRoles.Clinician);
        var other = await apps.CreateStaffAsync("Dr. Other Account", StaffRoles.Clinician);
        var first = await SignInAsync(apps.OnPrem, account);
        var second = await SignInAsync(apps.OnPrem, account);
        var elsewhere = await SignInAsync(apps.OnPrem, other);
        using var authenticator = new TestAuthenticator();
        using var begun = await OptionsToAddAsync(first.Client, "This Mac", Totp.Code(account.AuthenticatorKey));
        var (options, state) = await ReadyAsync(begun);
        var credential = authenticator.Create(options, WebApps.OnSiteOrigin);

        foreach (var client in new[] { second.Client, elsewhere.Client })
        {
            using var refused = await FinishAddAsync(client, credential, state);
            Assert.Contains("Not saved. Timed out. Try again.", Words(await ReadAsync(refused)));
        }

        using var tampered = await FinishAddAsync(first.Client, credential, state[..^4] + "AAAA");
        Assert.Contains("Not saved. Timed out.", Words(await ReadAsync(tampered)));
        Assert.Empty(await PasskeysAsync(account));
        Assert.Empty(await PasskeysAsync(other));

        // The sign-in it was made for still takes it.
        using var added = await FinishAddAsync(first.Client, credential, state);
        Assert.Contains("Passkey added.", Words(await ReadAsync(added)));
    }

    [Fact]
    public async Task A_passkey_needs_a_name_and_an_account_holds_at_most_ten()
    {
        var account = await apps.CreateStaffAsync("Dr. Ten Passkeys", StaffRoles.Clinician);
        var session = await SignInAsync(apps.OnPrem, account);

        foreach (var name in new[] { "  ", new string('n', StaffPasskeys.LongestName + 1) })
        {
            using var refused = await OptionsToAddAsync(session.Client, name, Totp.Code(account.AuthenticatorKey));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Contains("\"field\":\"Name\"", await refused.Content.ReadAsStringAsync(Cancel));
        }

        for (var added = 0; added < StaffPasskeys.MostPerAccount; added++)
        {
            using var authenticator = new TestAuthenticator();
            using var page = await AddAsync(session.Client, account, authenticator, name: $"Key {added}");
            Assert.Contains("Passkey added.", Words(await ReadAsync(page)));
        }

        using var eleventh = await OptionsToAddAsync(session.Client, "One more", Totp.Code(account.AuthenticatorKey));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, eleventh.StatusCode);
        Assert.Contains("You have the maximum of 10 passkeys", await eleventh.Content.ReadAsStringAsync(Cancel));
        var full = Words(await session.Client.GetStringAsync(PasskeysPage, Cancel));
        Assert.Contains("You have the maximum of 10 passkeys. Remove one to add another.", full);
        Assert.DoesNotContain("Add passkey</button>", await session.Client.GetStringAsync(PasskeysPage, Cancel));
    }

    [Fact]
    public async Task Another_accounts_passkey_is_not_removed()
    {
        var owner = await apps.CreateStaffAsync("Dr. Keeps A Passkey", StaffRoles.Clinician);
        var other = await apps.CreateStaffAsync("Dr. Removes Another's", StaffRoles.Clinician);
        using var authenticator = new TestAuthenticator();
        using (var added = await AddAsync((await SignInAsync(apps.OnPrem, owner)).Client, owner, authenticator))
        {
            Assert.Contains("Passkey added.", Words(await ReadAsync(added)));
        }

        var session = await SignInAsync(apps.OnPrem, other);
        using var refused = await PostFormAsync(session.Client, $"{PasskeysPage}?handler=Remove", PasskeysPage, ("id", Base64Url.EncodeToString(authenticator.CredentialId)));

        Assert.Contains("Not removed. Passkey not found.", Words(await ReadAsync(refused)));
        Assert.Single(await PasskeysAsync(owner));
        Assert.Empty(await EventsAsync(AuditActions.PasskeyRemoved, other));
    }

    [Fact]
    public async Task Off_site_both_posts_go_to_the_clinics_server_and_cut_off_neither_is_offered()
    {
        var account = await apps.CreateStaffAsync("Dr. Off-site Passkey", StaffRoles.Clinician);
        var session = await SignInAsync(apps.Cloud, account);
        using var authenticator = new TestAuthenticator();

        using var added = await AddAsync(session.Client, account, authenticator, origin: WebApps.OffSiteOrigin);

        Assert.Contains("Passkey added.", Words(await ReadAsync(added)));
        Assert.Contains($"POST {PasskeysPage}?handler=Options", apps.Forwarded);
        Assert.Contains($"POST {PasskeysPage}?handler=Add", apps.Forwarded);
        Assert.Single(await PasskeysAsync(account));

        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));
        var cutOff = await Client(apps.CloudCutOff, session.Jar).GetStringAsync(PasskeysPage, Cancel);
        Assert.Contains("Adding or removing a passkey needs the clinic's server.", Words(cutOff));
        Assert.Contains("This Mac", Words(cutOff));
        Assert.DoesNotContain("handler=Options", cutOff);
        Assert.DoesNotContain("handler=Remove", cutOff);
    }

    // A lost device may hold a passkey as well as the authenticator: an administrator's authenticator reset (at
    // /admin/staff, StaffAccounts) removes every passkey, each recorded as the administrator's doing, and the next
    // sign-in enrolls an authenticator, offering no passkey.
    [Fact]
    public async Task An_administrators_authenticator_reset_removes_the_accounts_passkeys_too()
    {
        var admin = await apps.CreateStaffAsync("Laptop Admin", StaffRoles.Admin);
        var adminSession = await SignInAsync(apps.OnPrem, admin);
        var account = await apps.CreateStaffAsync("Dr. Lost Laptop", StaffRoles.Clinician);
        var session = await SignInAsync(apps.OnPrem, account);
        using var laptop = new TestAuthenticator();
        using var phone = new TestAuthenticator();
        foreach (var (authenticator, name) in new[] { (laptop, "Grey laptop key"), (phone, "Blue phone key") })
        {
            using var added = await AddAsync(session.Client, account, authenticator, name);
            Assert.Contains("Passkey added.", Words(await ReadAsync(added)));
        }

        using var reset = await PostFormAsync(adminSession.Client, "/admin/staff?handler=ResetAuthenticator", "/admin/staff", ("id", account.Id));

        Assert.Contains("Authenticator reset for Dr. Lost Laptop. Their 2 passkeys were removed.", Words(await ReadAsync(reset)));
        Assert.Empty(await PasskeysAsync(account));
        Assert.Equal(2, await apps.AuditEventsAsync(AuditActions.PasskeyRemoved, actorId: admin.Id));
        // The audit page shows them, about no patient, with the passkey named by its hash (step 18a keeps such an event's
        // details), and never its name.
        var audit = await adminSession.Client.GetStringAsync($"/admin/audit?Action={AuditActions.PasskeyRemoved}", Cancel);
        Assert.Contains(StaffPasskeys.Reference(laptop.CredentialId), audit);
        Assert.Contains(StaffPasskeys.Reference(phone.CredentialId), audit);
        Assert.DoesNotContain("Grey laptop key", audit);
        Assert.DoesNotContain("Blue phone key", audit);
        using var next = await PostFormAsync(Client(apps.OnPrem, new CookieContainer()), "/sign-in", "/sign-in", ("Input.Email", account.Email), ("Input.Password", account.Password));
        Assert.Equal("/sign-in/enroll", PathOf(next));
    }

    // The final review's probe: five wrong codes lock the account, and from then on no code is checked, the right one
    // included, at an add's start or at its finish.
    [Fact]
    public async Task A_locked_account_is_refused_an_add_even_with_the_right_code()
    {
        var account = await apps.CreateStaffAsync("Dr. Locked Adding", StaffRoles.Clinician);
        var session = await SignInAsync(apps.OnPrem, account);
        using var authenticator = new TestAuthenticator();
        using var begun = await OptionsToAddAsync(session.Client, "This Mac", Totp.Code(account.AuthenticatorKey));
        var (options, state) = await ReadyAsync(begun);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var wrong = await OptionsToAddAsync(session.Client, "This Mac", Totp.WrongCode(account.AuthenticatorKey));
            Assert.Contains("Wrong code.", await wrong.Content.ReadAsStringAsync(Cancel));
        }

        using var refused = await OptionsToAddAsync(session.Client, "This Mac", Totp.Code(account.AuthenticatorKey));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var answer = JsonSerializer.Deserialize<JsonElement>(await refused.Content.ReadAsStringAsync(Cancel));
        Assert.Equal("Not saved. Account locked. Try again in 15 minutes.", answer.GetProperty("problem").GetString());
        Assert.False(answer.TryGetProperty("options", out _));

        using var finished = await FinishAddAsync(session.Client, authenticator.Create(options, WebApps.OnSiteOrigin), state);
        Assert.Contains("Not saved. Account locked.", Words(await ReadAsync(finished)));
        Assert.Empty(await PasskeysAsync(account));
        Assert.Equal(2, (await EventsAsync(AuditActions.PasskeyRefused, account)).Count(e => e.Reason == "locked out"));
    }

    // The final review's probe: wrong codes sent at once are each checked and counted, one after another (the account's
    // row is held), never lost to a failed update and answered 500; the fifth locks the account, and the rest are refused.
    [Fact]
    public async Task Many_wrong_codes_at_once_are_all_counted_and_the_fifth_locks_the_account()
    {
        var account = await apps.CreateStaffAsync("Dr. Guessing Codes", StaffRoles.Clinician);
        var session = await SignInAsync(apps.OnPrem, account);
        var token = Token(await session.Client.GetStringAsync(PasskeysPage, Cancel));
        const int Sent = 40;

        var answers = await Task.WhenAll(Enumerable.Range(0, Sent).Select(async _ =>
        {
            using var response = await session.Client.PostAsync($"{PasskeysPage}?handler=Options",
                Form(("name", "This Mac"), ("code", Totp.WrongCode(account.AuthenticatorKey)), ("__RequestVerificationToken", token)), Cancel);
            return (response.StatusCode, Body: await response.Content.ReadAsStringAsync(Cancel));
        }));

        Assert.All(answers, a => Assert.Equal(HttpStatusCode.UnprocessableEntity, a.StatusCode));
        Assert.Equal(5, answers.Count(a => a.Body.Contains("Wrong code.", StringComparison.Ordinal)));
        Assert.Equal(Sent - 5, answers.Count(a => a.Body.Contains("Account locked.", StringComparison.Ordinal)));
        var events = await EventsAsync(AuditActions.PasskeyRefused, account);
        Assert.Equal(5, events.Count(e => e.Reason == "wrong code"));
        Assert.Equal(Sent - 5, events.Count(e => e.Reason == "locked out"));
        await using var scope = apps.OnPrem.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        Assert.True(await users.IsLockedOutAsync((await users.FindByIdAsync(account.Id.ToString()))!));
    }

    // The final review's probe: two adds finishing at once with nine stored can't both pass the limit; the one that
    // does has its event, the other saves nothing.
    [Fact]
    public async Task Two_adds_finishing_at_once_with_nine_stored_leave_ten_each_with_its_event()
    {
        var account = await apps.CreateStaffAsync("Dr. Two At Once", StaffRoles.Clinician);
        var session = await SignInAsync(apps.OnPrem, account);
        for (var added = 0; added < StaffPasskeys.MostPerAccount - 1; added++)
        {
            using var authenticator = new TestAuthenticator();
            using var page = await AddAsync(session.Client, account, authenticator, name: $"Key {added}");
            Assert.Contains("Passkey added.", Words(await ReadAsync(page)));
        }

        using var first = new TestAuthenticator();
        using var second = new TestAuthenticator();
        var finishes = new List<(string Credential, string State)>();
        foreach (var authenticator in new[] { first, second })
        {
            using var begun = await OptionsToAddAsync(session.Client, "One of two", Totp.Code(account.AuthenticatorKey));
            var (options, state) = await ReadyAsync(begun);
            finishes.Add((authenticator.Create(options, WebApps.OnSiteOrigin), state));
        }

        var token = Token(await session.Client.GetStringAsync(PasskeysPage, Cancel));
        var pages = await Task.WhenAll(finishes.Select(async f =>
        {
            using var response = await session.Client.PostAsync($"{PasskeysPage}?handler=Add",
                Form(("credential", f.Credential), ("state", f.State), ("__RequestVerificationToken", token)), Cancel);
            return Words(await ReadAsync(response));
        }));

        Assert.Single(pages, p => p.Contains("Passkey added.", StringComparison.Ordinal));
        Assert.Single(pages, p => p.Contains("Not saved. You have the maximum of 10 passkeys", StringComparison.Ordinal));
        Assert.Equal(StaffPasskeys.MostPerAccount, (await PasskeysAsync(account)).Count);
        Assert.Equal(StaffPasskeys.MostPerAccount, (await EventsAsync(AuditActions.PasskeyAdded, account)).Count);
    }

    // The final review's reading: an add and a removal at once leave the rows and the events agreeing, each row with its
    // added event, the removed one with its removal.
    [Fact]
    public async Task An_add_and_a_removal_at_once_leave_the_rows_and_the_events_agreeing()
    {
        var account = await apps.CreateStaffAsync("Dr. Add And Remove", StaffRoles.Clinician);
        var session = await SignInAsync(apps.OnPrem, account);
        using var kept = new TestAuthenticator();
        using var removed = new TestAuthenticator();
        using (var page = await AddAsync(session.Client, account, removed, name: "Old key"))
        {
            Assert.Contains("Passkey added.", Words(await ReadAsync(page)));
        }

        using var begun = await OptionsToAddAsync(session.Client, "New key", Totp.Code(account.AuthenticatorKey));
        var (options, state) = await ReadyAsync(begun);
        var credential = kept.Create(options, WebApps.OnSiteOrigin);
        var token = Token(await session.Client.GetStringAsync(PasskeysPage, Cancel));

        var posts = await Task.WhenAll(
            session.Client.PostAsync($"{PasskeysPage}?handler=Add", Form(("credential", credential), ("state", state), ("__RequestVerificationToken", token)), Cancel),
            session.Client.PostAsync($"{PasskeysPage}?handler=Remove", Form(("id", Base64Url.EncodeToString(removed.CredentialId)), ("__RequestVerificationToken", token)), Cancel));
        var pages = new List<string>();
        foreach (var post in posts)
        {
            pages.Add(Words(await ReadAsync(post)));
            post.Dispose();
        }

        Assert.Contains("Passkey added.", pages[0]);
        Assert.Contains("Passkey removed.", pages[1]);

        var stored = Assert.Single(await PasskeysAsync(account));
        Assert.Equal(kept.CredentialId, stored.CredentialId);
        Assert.Equal(
            new[] { StaffPasskeys.Reference(removed.CredentialId), StaffPasskeys.Reference(kept.CredentialId) }.Order(),
            (await EventsAsync(AuditActions.PasskeyAdded, account)).Select(e => JsonSerializer.Deserialize<JsonElement>(e.Details!).GetProperty("passkey").GetString()!).Order());
        Assert.Equal(
            [StaffPasskeys.Reference(removed.CredentialId)],
            (await EventsAsync(AuditActions.PasskeyRemoved, account)).Select(e => JsonSerializer.Deserialize<JsonElement>(e.Details!).GetProperty("passkey").GetString()!));
    }

    const string ResetUrl = "/admin/staff?handler=ResetAuthenticator";

    const string AccountChanged = "Not saved. Your sign-in settings changed. Sign in again, then add the passkey.";

    // Step 19.1, the final review's probe: an add finishing as an administrator resets the authenticator, the add holding
    // the account first. The reset waits for it, then finds the passkey it saved and removes it with the others, its
    // page counting it: no passkey is left without its authenticator (decision 11). Before, the reset read the passkeys
    // before it held the account and left the new one, or, with none before, failed on the account's concurrency stamp,
    // a 500, and reset nothing.
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public async Task A_reset_waiting_behind_a_finishing_add_removes_that_passkey_too(int before)
    {
        var admin = await apps.CreateStaffAsync($"Waiting Reset Admin {before}", StaffRoles.Admin);
        var adminSession = await SignInAsync(apps.OnPrem, admin);
        var account = await apps.CreateStaffAsync($"Dr. Adds As Reset {before}", StaffRoles.Clinician);
        var session = await SignInAsync(apps.OnPrem, account);
        using var old = new TestAuthenticator();
        using var fresh = new TestAuthenticator();
        if (before == 1)
        {
            using var page = await AddAsync(session.Client, account, old, name: "Old key");
            Assert.Contains("Passkey added.", Words(await ReadAsync(page)));
        }

        using var begun = await OptionsToAddAsync(session.Client, "New key", Totp.Code(account.AuthenticatorKey));
        var (options, state) = await ReadyAsync(begun);
        var credential = fresh.Create(options, WebApps.OnSiteOrigin);
        var token = Token(await session.Client.GetStringAsync(PasskeysPage, Cancel));
        var adminToken = Token(await adminSession.Client.GetStringAsync("/admin/staff", Cancel));

        Task<HttpResponseMessage> adding, resetting;
        await using (var hold = await HoldAccountAsync(apps, account.Id))
        {
            adding = session.Client.PostAsync($"{PasskeysPage}?handler=Add", Form(("credential", credential), ("state", state), ("__RequestVerificationToken", token)), Cancel);
            await WaitingForLocksAsync(apps, 1);
            resetting = adminSession.Client.PostAsync(ResetUrl, Form(("id", account.Id), ("__RequestVerificationToken", adminToken)), Cancel);
            await WaitingForLocksAsync(apps, 2);
            await hold.Transaction.CommitAsync(Cancel);
        }

        using var added = await adding;
        using var reset = await resetting;

        Assert.Contains("Passkey added.", Words(await ReadAsync(added)));
        Assert.Contains(before == 1 ? "Their 2 passkeys were removed." : "Their passkey was removed.", Words(await ReadAsync(reset)));
        Assert.Empty(await PasskeysAsync(account));
        Assert.False((await AccountAsync(account)).TwoFactorEnabled);
        Assert.Equal(before + 1, await apps.AuditEventsAsync(AuditActions.PasskeyRemoved, actorId: admin.Id));
    }

    // Step 19.1, the final review's probe: an add begun before an administrator's reset and finished after it, in the
    // session that began it, which goes on until its next account check, up to 5 minutes: refused, and so it is once the
    // account has enrolled a new authenticator, since the add's state carries the account's security stamp from when
    // its code was checked, which the reset and the enrolment each change. Recorded, counting nothing towards the
    // lockout. Before, it was saved, and once the account had enrolled again the password and that passkey signed in.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_add_begun_before_a_reset_is_refused_at_its_finish(bool enrolledAgain)
    {
        var admin = await apps.CreateStaffAsync($"Resetting Admin {enrolledAgain}", StaffRoles.Admin);
        var adminSession = await SignInAsync(apps.OnPrem, admin);
        var account = await apps.CreateStaffAsync($"Dr. Lost Device {enrolledAgain}", StaffRoles.Clinician);
        var session = await SignInAsync(apps.OnPrem, account);
        using var old = new TestAuthenticator();
        using var left = new TestAuthenticator();
        using (var page = await AddAsync(session.Client, account, old, name: "Old key"))
        {
            Assert.Contains("Passkey added.", Words(await ReadAsync(page)));
        }

        using var begun = await OptionsToAddAsync(session.Client, "Begun before the reset", Totp.Code(account.AuthenticatorKey));
        var (options, state) = await ReadyAsync(begun);
        using (var reset = await PostFormAsync(adminSession.Client, ResetUrl, "/admin/staff", ("id", account.Id)))
        {
            Assert.Contains("Their passkey was removed.", Words(await ReadAsync(reset)));
        }

        if (enrolledAgain)
        {
            var owner = Client(apps.OnPrem, new CookieContainer());
            using var enrollPage = await PostFormAsync(owner, "/sign-in", "/sign-in", ("Input.Email", account.Email), ("Input.Password", account.Password));
            Assert.Equal("/sign-in/enroll", PathOf(enrollPage));
            string key;
            await using (var scope = apps.OnPrem.Services.CreateAsyncScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
                key = (await users.GetAuthenticatorKeyAsync((await users.FindByIdAsync(account.Id.ToString()))!))!;
            }

            using var enrolled = await PostFormAsync(owner, "/sign-in/enroll", enrollPage, ("Code", Totp.Code(key)));
            Assert.Contains("Your recovery codes", await ReadAsync(enrolled));
        }

        var failedBefore = (await AccountAsync(account)).AccessFailedCount;
        using var finished = await FinishAddAsync(session.Client, left.Create(options, WebApps.OnSiteOrigin), state);

        Assert.Contains(AccountChanged, Words(await ReadAsync(finished)));
        Assert.Empty(await PasskeysAsync(account));
        Assert.Equal(new (string?, string?)[] { ("account changed", null) }, await EventsAsync(AuditActions.PasskeyRefused, account));
        Assert.Equal(failedBefore, (await AccountAsync(account)).AccessFailedCount);
        if (enrolledAgain)
        {
            // The account's next password step offers no passkey.
            var (client, codePage) = await PasswordAsync(apps.OnPrem, account);
            using var none = await OptionsToSignInAsync(client, codePage);
            Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        }
    }

    // Step 19.1, the final review's probe, at once: whichever of the add and the reset holds the account first, no
    // passkey outlives the reset, nothing is answered 500, and the reset's page counts what it removed, the added one
    // with the rest when the add came first. Before, the new passkey was left in 23 of 24 runs.
    [Fact]
    public async Task An_add_finishing_as_a_reset_runs_never_outlives_it()
    {
        var admin = await apps.CreateStaffAsync("Racing Reset Admin", StaffRoles.Admin);
        var adminSession = await SignInAsync(apps.OnPrem, admin);
        for (var run = 0; run < 10; run++)
        {
            var account = await apps.CreateStaffAsync($"Dr. Races A Reset {run}", StaffRoles.Clinician);
            var session = await SignInAsync(apps.OnPrem, account);
            using var old = new TestAuthenticator();
            using var fresh = new TestAuthenticator();
            using (var page = await AddAsync(session.Client, account, old, name: "Old key"))
            {
                Assert.Contains("Passkey added.", Words(await ReadAsync(page)));
            }

            using var begun = await OptionsToAddAsync(session.Client, "New key", Totp.Code(account.AuthenticatorKey));
            var (options, state) = await ReadyAsync(begun);
            var credential = fresh.Create(options, WebApps.OnSiteOrigin);
            var token = Token(await session.Client.GetStringAsync(PasskeysPage, Cancel));
            var adminToken = Token(await adminSession.Client.GetStringAsync("/admin/staff", Cancel));

            var posts = await Task.WhenAll(
                session.Client.PostAsync($"{PasskeysPage}?handler=Add", Form(("credential", credential), ("state", state), ("__RequestVerificationToken", token)), Cancel),
                adminSession.Client.PostAsync(ResetUrl, Form(("id", account.Id), ("__RequestVerificationToken", adminToken)), Cancel));
            var pages = new List<string>();
            foreach (var post in posts)
            {
                Assert.True(post.StatusCode == HttpStatusCode.OK, $"Run {run}: {(int)post.StatusCode} from {PathOf(post)}.");
                pages.Add(Words(await ReadAsync(post)));
                post.Dispose();
            }

            Assert.True((await PasskeysAsync(account)).Count == 0, $"Run {run}: a passkey outlived the reset.");
            var addedFirst = pages[0].Contains("Passkey added.", StringComparison.Ordinal);
            Assert.True(addedFirst || pages[0].Contains(AccountChanged, StringComparison.Ordinal), $"Run {run}: the add was neither saved nor refused for the reset.");
            Assert.Contains(addedFirst ? "Their 2 passkeys were removed." : "Their passkey was removed.", pages[1]);
            Assert.Equal(addedFirst ? 2 : 1, (await EventsAsync(AuditActions.PasskeyRemoved, account)).Count);
        }
    }

    // Step 19.1, beside the final review's probe: two administrators resetting the same account at once, which has no
    // passkey, the second waiting behind the first. Both are answered, and the account is reset. Before, the second
    // failed on the account's concurrency stamp with a 500.
    [Fact]
    public async Task Two_administrators_resetting_one_account_at_once_both_reset_it()
    {
        var first = await apps.CreateStaffAsync("First Resetting Admin", StaffRoles.Admin);
        var second = await apps.CreateStaffAsync("Second Resetting Admin", StaffRoles.Admin);
        var account = await apps.CreateStaffAsync("Dr. Reset Twice", StaffRoles.Clinician);
        var sessions = new[] { await SignInAsync(apps.OnPrem, first), await SignInAsync(apps.OnPrem, second) };
        var tokens = new List<string>();
        foreach (var session in sessions)
        {
            tokens.Add(Token(await session.Client.GetStringAsync("/admin/staff", Cancel)));
        }

        var resets = new List<Task<HttpResponseMessage>>();
        await using (var hold = await HoldAccountAsync(apps, account.Id))
        {
            for (var i = 0; i < 2; i++)
            {
                resets.Add(sessions[i].Client.PostAsync(ResetUrl, Form(("id", account.Id), ("__RequestVerificationToken", tokens[i])), Cancel));
                await WaitingForLocksAsync(apps, i + 1);
            }

            await hold.Transaction.CommitAsync(Cancel);
        }

        foreach (var reset in await Task.WhenAll(resets))
        {
            Assert.Contains("Authenticator reset for Dr. Reset Twice.", Words(await ReadAsync(reset)));
            reset.Dispose();
        }

        Assert.False((await AccountAsync(account)).TwoFactorEnabled);
        Assert.Equal(2, (await EventsAsync(AuditActions.AuthenticatorReset, account)).Count);
    }

    // Step 19.1, the final review's probe kept as it passed: two administrators resetting each other at once, each with a
    // passkey. Each reset holds the other's account only, so neither waits for the other, and both complete.
    [Fact]
    public async Task Two_administrators_resetting_each_other_at_once_both_resets_complete()
    {
        for (var run = 0; run < 8; run++)
        {
            var admins = new[] { await apps.CreateStaffAsync($"Admin A {run}", StaffRoles.Admin), await apps.CreateStaffAsync($"Admin B {run}", StaffRoles.Admin) };
            var sessions = new List<Session>();
            var tokens = new List<string>();
            foreach (var admin in admins)
            {
                var session = await SignInAsync(apps.OnPrem, admin);
                using var authenticator = new TestAuthenticator();
                using (var page = await AddAsync(session.Client, admin, authenticator, name: "Admin key"))
                {
                    Assert.Contains("Passkey added.", Words(await ReadAsync(page)));
                }

                sessions.Add(session);
                tokens.Add(Token(await session.Client.GetStringAsync("/admin/staff", Cancel)));
            }

            var resets = await Task.WhenAll(
                sessions[0].Client.PostAsync(ResetUrl, Form(("id", admins[1].Id), ("__RequestVerificationToken", tokens[0])), Cancel),
                sessions[1].Client.PostAsync(ResetUrl, Form(("id", admins[0].Id), ("__RequestVerificationToken", tokens[1])), Cancel));
            foreach (var reset in resets)
            {
                Assert.True(reset.StatusCode == HttpStatusCode.OK, $"Run {run}: {(int)reset.StatusCode}.");
                Assert.Contains("Their passkey was removed.", Words(await ReadAsync(reset)));
                reset.Dispose();
            }

            foreach (var admin in admins)
            {
                Assert.False((await AccountAsync(admin)).TwoFactorEnabled);
                Assert.Empty(await PasskeysAsync(admin));
            }
        }
    }

    // Step 19.1's final review: an administrator's disable or password reissue as an add finishes, at once and with the
    // add holding the account first. Each action holds the account first now, as the reset does: both are answered, the
    // account ends disabled or its password reissued, and the add is either saved first or refused (locked out, or the
    // account changed). Before, the action failed on the account's concurrency stamp: a disable answered 500 and left
    // the account enabled, a reissue said "Optimistic concurrency failure, object has been modified." and left the
    // password.
    [Theory]
    [InlineData("Disable", false)]
    [InlineData("Reissue", false)]
    [InlineData("Disable", true)]
    [InlineData("Reissue", true)]
    public async Task An_administrators_disable_or_reissue_as_an_add_finishes_goes_through(string action, bool addFirst)
    {
        var admin = await apps.CreateStaffAsync($"{action} Admin {addFirst}", StaffRoles.Admin);
        var adminSession = await SignInAsync(apps.OnPrem, admin);
        for (var run = 0; run < (addFirst ? 1 : 10); run++)
        {
            var account = await apps.CreateStaffAsync($"Dr. {action} As Add {addFirst} {run}", StaffRoles.Clinician);
            var session = await SignInAsync(apps.OnPrem, account);
            using var authenticator = new TestAuthenticator();
            using var begun = await OptionsToAddAsync(session.Client, "Racing", Totp.Code(account.AuthenticatorKey));
            var (options, state) = await ReadyAsync(begun);
            var credential = authenticator.Create(options, WebApps.OnSiteOrigin);
            var token = Token(await session.Client.GetStringAsync(PasskeysPage, Cancel));
            var adminToken = Token(await adminSession.Client.GetStringAsync("/admin/staff", Cancel));
            Task<HttpResponseMessage> Add() => session.Client.PostAsync(
                $"{PasskeysPage}?handler=Add", Form(("credential", credential), ("state", state), ("__RequestVerificationToken", token)), Cancel);
            Task<HttpResponseMessage> Act() => adminSession.Client.PostAsync(
                $"/admin/staff?handler={action}", Form(("id", account.Id), ("__RequestVerificationToken", adminToken)), Cancel);

            HttpResponseMessage[] posts;
            if (addFirst)
            {
                Task<HttpResponseMessage> adding, acting;
                await using (var hold = await HoldAccountAsync(apps, account.Id))
                {
                    adding = Add();
                    await WaitingForLocksAsync(apps, 1);
                    acting = Act();
                    await WaitingForLocksAsync(apps, 2);
                    await hold.Transaction.CommitAsync(Cancel);
                }

                posts = [await adding, await acting];
            }
            else
            {
                posts = await Task.WhenAll(Add(), Act());
            }

            Assert.True(posts.All(p => p.StatusCode == HttpStatusCode.OK), $"Run {run}: {string.Join(", ", posts.Select(p => (int)p.StatusCode))}.");
            var (add, acted) = (Words(await ReadAsync(posts[0])), Words(await ReadAsync(posts[1])));
            Array.ForEach(posts, p => p.Dispose());
            var user = await AccountAsync(account);
            var added = add.Contains("Passkey added.", StringComparison.Ordinal);
            Assert.True(added || add.Contains(action == "Disable" ? "Account locked" : AccountChanged, StringComparison.Ordinal),
                $"Run {run}: the add was neither saved nor refused for the {action.ToLowerInvariant()}.");
            Assert.Equal(added ? 1 : 0, (await PasskeysAsync(account)).Count);
            Assert.True(!addFirst || added, "The add held the account first, and should have been saved.");
            Assert.DoesNotContain("Optimistic concurrency failure", acted);
            if (action == "Disable")
            {
                Assert.Contains("is disabled.", acted);
                Assert.True(StaffAccounts.IsDisabled(user), $"Run {run}: the account isn't disabled.");
            }
            else
            {
                Assert.Contains("/sign-in/setup?user=", acted);
                Assert.True(user.PasswordHash is null, $"Run {run}: the password wasn't reissued.");
            }
        }
    }

    // Beside it, kept as a guard: enabling an account as an add begun before its disable finishes. Both are answered, and
    // the add is refused, as the account was locked out or has changed since.
    [Fact]
    public async Task An_administrators_enable_as_an_add_finishes_goes_through()
    {
        var admin = await apps.CreateStaffAsync("Enabling Admin", StaffRoles.Admin);
        var adminSession = await SignInAsync(apps.OnPrem, admin);
        for (var run = 0; run < 10; run++)
        {
            var account = await apps.CreateStaffAsync($"Dr. Enabled As Add {run}", StaffRoles.Clinician);
            var session = await SignInAsync(apps.OnPrem, account);
            using var authenticator = new TestAuthenticator();
            using var begun = await OptionsToAddAsync(session.Client, "Racing", Totp.Code(account.AuthenticatorKey));
            var (options, state) = await ReadyAsync(begun);
            var credential = authenticator.Create(options, WebApps.OnSiteOrigin);
            var token = Token(await session.Client.GetStringAsync(PasskeysPage, Cancel));
            using (var disabled = await PostFormAsync(adminSession.Client, "/admin/staff?handler=Disable", "/admin/staff", ("id", account.Id)))
            {
                Assert.Contains("is disabled", Words(await ReadAsync(disabled)));
            }

            var adminToken = Token(await adminSession.Client.GetStringAsync("/admin/staff", Cancel));
            var posts = await Task.WhenAll(
                session.Client.PostAsync($"{PasskeysPage}?handler=Add", Form(("credential", credential), ("state", state), ("__RequestVerificationToken", token)), Cancel),
                adminSession.Client.PostAsync("/admin/staff?handler=Enable", Form(("id", account.Id), ("__RequestVerificationToken", adminToken)), Cancel));

            Assert.True(posts.All(p => p.StatusCode == HttpStatusCode.OK), $"Run {run}: {string.Join(", ", posts.Select(p => (int)p.StatusCode))}.");
            Assert.Contains("Not saved.", Words(await ReadAsync(posts[0])));
            Assert.Contains("can sign in again.", Words(await ReadAsync(posts[1])));
            Array.ForEach(posts, p => p.Dispose());
            Assert.Empty(await PasskeysAsync(account));
            Assert.False(StaffAccounts.IsDisabled(await AccountAsync(account)));
        }
    }

    // The two ceremonies' states are not interchangeable: an add's state is refused at sign-in, and a sign-in's state,
    // which carries no security stamp, at an add's finish (step 19.1's check of the stamp comes after its purpose).
    [Fact]
    public async Task A_sign_ins_state_is_refused_at_an_add_and_an_adds_state_at_sign_in()
    {
        var account = await apps.CreateStaffAsync("Dr. Two Ceremonies", StaffRoles.Clinician);
        var session = await SignInAsync(apps.OnPrem, account);
        using var authenticator = new TestAuthenticator();
        using (var page = await AddAsync(session.Client, account, authenticator, name: "This Mac"))
        {
            Assert.Contains("Passkey added.", Words(await ReadAsync(page)));
        }

        var (client, codePage) = await PasswordAsync(apps.OnPrem, account);
        using var signInOptions = await OptionsToSignInAsync(client, codePage);
        var (requestOptions, signInState) = await ReadyAsync(signInOptions);
        using var addOptions = await OptionsToAddAsync(session.Client, "Another", Totp.Code(account.AuthenticatorKey));
        var (creationOptions, addState) = await ReadyAsync(addOptions);
        using var another = new TestAuthenticator();

        using var asAdd = await FinishAddAsync(session.Client, another.Create(creationOptions, WebApps.OnSiteOrigin), signInState);
        using var asSignIn = await PasskeyAsync(client, codePage, authenticator.Get(requestOptions, WebApps.OnSiteOrigin), addState);

        Assert.Contains("Not saved. Timed out. Try again.", Words(await ReadAsync(asAdd)));
        Assert.Equal(CodePage, PathOf(asSignIn));
        Assert.Contains("Passkey not accepted.", Words(await ReadAsync(asSignIn)));
        Assert.Single(await PasskeysAsync(account));
    }

    async Task<StaffUser> AccountAsync(StaffAccount account)
    {
        await using var scope = apps.OnPrem.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>().FindByIdAsync(account.Id.ToString()))!;
    }

    async Task<IList<UserPasskeyInfo>> PasskeysAsync(StaffAccount account)
    {
        await using var scope = apps.OnPrem.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        return await users.GetPasskeysAsync((await users.FindByIdAsync(account.Id.ToString()))!);
    }

    // The account's events of one kind, oldest first: why, and the details as stored.
    async Task<List<(string? Reason, string? Details)>> EventsAsync(string action, StaffAccount account)
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "SELECT reason, details::text FROM ehr.audit_events WHERE action = @action AND row_id = @account AND table_name = 'staff_users' ORDER BY occurred_at, id",
            connection);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("account", account.Id);
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        var events = new List<(string?, string?)>();
        while (await reader.ReadAsync(Cancel))
        {
            events.Add((reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return events;
    }

    // Every event's details that names the account, as actor or row.
    async Task<List<string?>> AllDetailsAsync(StaffAccount account)
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "SELECT details::text FROM ehr.audit_events WHERE actor_id = @account OR row_id = @account", connection);
        command.Parameters.AddWithValue("account", account.Id);
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        var details = new List<string?>();
        while (await reader.ReadAsync(Cancel))
        {
            details.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        }

        return details;
    }
}
