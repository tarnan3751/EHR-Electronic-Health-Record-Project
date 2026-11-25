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
// authenticator reset; recorded without its name; and off-site, both posts forwarded to the clinic's server, or, cut
// off from it, not offered.
public class PasskeyPageTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_passkey_is_added_with_a_current_code_listed_and_removed_by_its_owner()
    {
        var account = await apps.CreateStaffAsync("Dr. Adds A Passkey", StaffRoles.Clinician);
        var session = await SignInAsync(apps.OnPrem, account);
        using var authenticator = new TestAuthenticator();

        using var added = await AddAsync(session.Client, account, authenticator, name: "This Mac");

        var page = Words(await ReadAsync(added));
        Assert.Contains("Passkey added: use it at your next sign-in, after your password.", page);
        Assert.Contains("This Mac", page);
        Assert.Contains("Removing a passkey doesn't sign out a session already signed in with it.", page);
        var stored = Assert.Single(await PasskeysAsync(account));
        Assert.Equal(authenticator.CredentialId, stored.CredentialId);
        Assert.True(stored.IsUserVerified);
        var reference = StaffPasskeys.Reference(authenticator.CredentialId);
        Assert.Equal(new (string?, string?)[] { (null, $$"""{"passkey": "{{reference}}"}""") }, await EventsAsync(AuditActions.PasskeyAdded, account));

        using var removed = await PostFormAsync(session.Client, $"{PasskeysPage}?handler=Remove", PasskeysPage, ("id", Base64Url.EncodeToString(authenticator.CredentialId)));

        Assert.Contains("Passkey removed: it can't be used to sign in any more.", Words(await ReadAsync(removed)));
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
            Assert.Equal("Not saved: that code did not match. Enter the current code from your authenticator app.", answer.GetProperty("problem").GetString());
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

            Assert.Contains("Not saved: the passkey wasn't accepted. A passkey has to ask for your PIN, fingerprint or face. Try again.", Words(await ReadAsync(refused)));
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
            Assert.Contains("Not saved: the passkey took too long, or this page was open too long. Try again.", Words(await ReadAsync(refused)));
        }

        using var tampered = await FinishAddAsync(first.Client, credential, state[..^4] + "AAAA");
        Assert.Contains("Not saved: the passkey took too long", Words(await ReadAsync(tampered)));
        Assert.Empty(await PasskeysAsync(account));
        Assert.Empty(await PasskeysAsync(other));

        // The sign-in it was made for still takes it.
        using var added = await FinishAddAsync(first.Client, credential, state);
        Assert.Contains("Passkey added:", Words(await ReadAsync(added)));
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
            Assert.Contains("Passkey added:", Words(await ReadAsync(page)));
        }

        using var eleventh = await OptionsToAddAsync(session.Client, "One more", Totp.Code(account.AuthenticatorKey));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, eleventh.StatusCode);
        Assert.Contains("an account can have at most 10 passkeys", await eleventh.Content.ReadAsStringAsync(Cancel));
        var full = Words(await session.Client.GetStringAsync(PasskeysPage, Cancel));
        Assert.Contains("You have 10 passkeys, the most an account can have. Remove one to add another.", full);
        Assert.DoesNotContain("Add a passkey</button>", await session.Client.GetStringAsync(PasskeysPage, Cancel));
    }

    [Fact]
    public async Task Another_accounts_passkey_is_not_removed()
    {
        var owner = await apps.CreateStaffAsync("Dr. Keeps A Passkey", StaffRoles.Clinician);
        var other = await apps.CreateStaffAsync("Dr. Removes Another's", StaffRoles.Clinician);
        using var authenticator = new TestAuthenticator();
        using (var added = await AddAsync((await SignInAsync(apps.OnPrem, owner)).Client, owner, authenticator))
        {
            Assert.Contains("Passkey added:", Words(await ReadAsync(added)));
        }

        var session = await SignInAsync(apps.OnPrem, other);
        using var refused = await PostFormAsync(session.Client, $"{PasskeysPage}?handler=Remove", PasskeysPage, ("id", Base64Url.EncodeToString(authenticator.CredentialId)));

        Assert.Contains("Not removed: that passkey isn't one of yours.", Words(await ReadAsync(refused)));
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

        Assert.Contains("Passkey added:", Words(await ReadAsync(added)));
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
            Assert.Contains("Passkey added:", Words(await ReadAsync(added)));
        }

        using var reset = await PostFormAsync(adminSession.Client, "/admin/staff?handler=ResetAuthenticator", "/admin/staff", ("id", account.Id));

        Assert.Contains("Dr. Lost Laptop will set up an authenticator app again at their next sign-in. Their 2 passkeys were removed.", Words(await ReadAsync(reset)));
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
            Assert.Contains("that code did not match", await wrong.Content.ReadAsStringAsync(Cancel));
        }

        using var refused = await OptionsToAddAsync(session.Client, "This Mac", Totp.Code(account.AuthenticatorKey));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var answer = JsonSerializer.Deserialize<JsonElement>(await refused.Content.ReadAsStringAsync(Cancel));
        Assert.Equal("Not saved: this account is locked after too many failed attempts. Try again in 15 minutes.", answer.GetProperty("problem").GetString());
        Assert.False(answer.TryGetProperty("options", out _));

        using var finished = await FinishAddAsync(session.Client, authenticator.Create(options, WebApps.OnSiteOrigin), state);
        Assert.Contains("Not saved: this account is locked after too many failed attempts.", Words(await ReadAsync(finished)));
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
        Assert.Equal(5, answers.Count(a => a.Body.Contains("that code did not match", StringComparison.Ordinal)));
        Assert.Equal(Sent - 5, answers.Count(a => a.Body.Contains("this account is locked", StringComparison.Ordinal)));
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
            Assert.Contains("Passkey added:", Words(await ReadAsync(page)));
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

        Assert.Single(pages, p => p.Contains("Passkey added:", StringComparison.Ordinal));
        Assert.Single(pages, p => p.Contains("an account can have at most 10 passkeys", StringComparison.Ordinal));
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
            Assert.Contains("Passkey added:", Words(await ReadAsync(page)));
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

        Assert.Contains("Passkey added:", pages[0]);
        Assert.Contains("Passkey removed:", pages[1]);

        var stored = Assert.Single(await PasskeysAsync(account));
        Assert.Equal(kept.CredentialId, stored.CredentialId);
        Assert.Equal(
            new[] { StaffPasskeys.Reference(removed.CredentialId), StaffPasskeys.Reference(kept.CredentialId) }.Order(),
            (await EventsAsync(AuditActions.PasskeyAdded, account)).Select(e => JsonSerializer.Deserialize<JsonElement>(e.Details!).GetProperty("passkey").GetString()!).Order());
        Assert.Equal(
            [StaffPasskeys.Reference(removed.CredentialId)],
            (await EventsAsync(AuditActions.PasskeyRemoved, account)).Select(e => JsonSerializer.Deserialize<JsonElement>(e.Details!).GetProperty("passkey").GetString()!));
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
