using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Domain.Accounts;
using Ehr.Testing;
using Ehr.Web.CloudAudit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.PasskeyFlows;

namespace Ehr.Web.Tests;

// Staff sign-in, step by step (Areas/Clinical/Pages/Account, StaffSignIn): the password, the second factor, what the
// bootstrap admin and an invited account have to do first, and what the audit log says about it.
public partial class StaffSignInTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_wrong_password_is_refused_and_recorded()
    {
        var account = await apps.CreateStaffAsync("Dr. Wrong", StaffRoles.Clinician);
        var client = Client(apps.OnPrem, new CookieContainer());

        using var response = await PostFormAsync(client, "/sign-in", "/sign-in", ("Input.Email", account.Email), ("Input.Password", "not-the-password"));

        Assert.Contains("Wrong email or password.", await ReadAsync(response));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.SignInFailed, actorId: account.Id));
    }

    [Fact]
    public async Task An_unknown_address_gets_the_same_answer()
    {
        var client = Client(apps.OnPrem, new CookieContainer());

        using var response = await PostFormAsync(client, "/sign-in", "/sign-in", ("Input.Email", WebApps.NewEmail()), ("Input.Password", "not-the-password"));

        Assert.Contains("Wrong email or password.", await ReadAsync(response));
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account()
    {
        var account = await apps.CreateStaffAsync("Dr. Locked", StaffRoles.Clinician);
        var client = Client(apps.OnPrem, new CookieContainer());
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var wrong = await PostFormAsync(client, "/sign-in", "/sign-in", ("Input.Email", account.Email), ("Input.Password", "not-the-password"));
        }

        using var response = await PostFormAsync(client, "/sign-in", "/sign-in", ("Input.Email", account.Email), ("Input.Password", account.Password));

        Assert.Contains("This account is locked", await ReadAsync(response));
    }

    // Wrong codes lock the account as wrong passwords do, and then the right code signs it in no more than the right
    // password would (step 19's fix: before it, the code step took a correct code from a locked account).
    [Fact]
    public async Task Five_wrong_codes_lock_the_account_and_then_the_right_code_is_refused()
    {
        var account = await apps.CreateStaffAsync("Dr. Guessed Codes", StaffRoles.Clinician);
        var client = Client(apps.OnPrem, new CookieContainer());
        using var codePage = await PostFormAsync(client, "/sign-in", "/sign-in", ("Input.Email", account.Email), ("Input.Password", account.Password));
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var wrong = await PostFormAsync(client, "/sign-in/code", "/sign-in/code", ("Code", Totp.WrongCode(account.AuthenticatorKey)));
            Assert.Contains("That code did not match.", await ReadAsync(wrong));
        }

        using var response = await PostFormAsync(client, "/sign-in/code", "/sign-in/code", ("Code", Totp.Code(account.AuthenticatorKey)));

        Assert.Equal("/sign-in/code", PathOf(response));
        Assert.Contains("This account is locked after too many failed attempts. Try again in 15 minutes.", await ReadAsync(response));
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.SignIn, actorId: account.Id));
        Assert.Contains("locked out", await ReasonsAsync(account));
    }

    // The final review's note, at the code step: codes sent at once are each checked and counted, one after another (the
    // account's row is held), never lost to a failed update and answered 500; the fifth wrong one locks the account.
    [Fact]
    public async Task Wrong_codes_sent_at_once_are_all_counted_and_the_fifth_locks_the_account()
    {
        var account = await apps.CreateStaffAsync("Dr. Codes At Once", StaffRoles.Clinician);
        var client = Client(apps.OnPrem, new CookieContainer());
        using var codePage = await PostFormAsync(client, "/sign-in", "/sign-in", ("Input.Email", account.Email), ("Input.Password", account.Password));
        var token = Token(await codePage.Content.ReadAsStringAsync(Cancel));
        const int Sent = 20;

        var answers = await Task.WhenAll(Enumerable.Range(0, Sent).Select(async _ =>
        {
            using var response = await client.PostAsync("/sign-in/code",
                Form(("Code", Totp.WrongCode(account.AuthenticatorKey)), ("__RequestVerificationToken", token)), Cancel);
            return (response.StatusCode, Body: await response.Content.ReadAsStringAsync(Cancel));
        }));

        Assert.All(answers, a => Assert.Equal(HttpStatusCode.OK, a.StatusCode));
        Assert.Equal(5, answers.Count(a => a.Body.Contains("That code did not match.", StringComparison.Ordinal)));
        Assert.Equal(Sent - 5, answers.Count(a => a.Body.Contains("This account is locked", StringComparison.Ordinal)));
        var reasons = await ReasonsAsync(account);
        Assert.Equal(5, reasons.Count(r => r == "wrong code"));
        Assert.Equal(Sent - 5, reasons.Count(r => r == "locked out"));
    }

    // Step 19.1, the final review's probe: the same passkey answer sent twice at once, from one password step, signs in
    // once. The second is checked against the count the first has just stored, and refused as an answer used again is
    // (decision 2). Before, both read the count before they held the account, and both signed in, in 24 of 24 runs.
    [Fact]
    public async Task The_same_passkey_answer_sent_twice_at_once_signs_in_once()
    {
        for (var run = 0; run < 10; run++)
        {
            var account = await apps.CreateStaffAsync($"Dr. Twice At Once {run}", StaffRoles.Clinician);
            using var authenticator = new TestAuthenticator();
            using (var added = await AddAsync((await SignInAsync(apps.OnPrem, account)).Client, account, authenticator))
            {
                Assert.Contains("Passkey added:", Words(await ReadAsync(added)));
            }

            var (client, codePage) = await PasswordAsync(apps.OnPrem, account);
            using var optionsResponse = await OptionsToSignInAsync(client, codePage);
            var (options, state) = await ReadyAsync(optionsResponse);
            var credential = authenticator.Get(options, WebApps.OnSiteOrigin);

            var answers = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
            {
                using var response = await PasskeyAsync(client, codePage, credential, state);
                return (Path: PathOf(response), Words: Words(await ReadAsync(response)));
            }));

            Assert.True(answers.Count(a => a.Path == "/patients") == 1, $"Run {run}: signed in {answers.Count(a => a.Path == "/patients")} times.");
            Assert.Single(answers, a => a.Words.Contains("That passkey didn't work.", StringComparison.Ordinal));
            // The add's sign-in, with the code, and one with the passkey.
            Assert.Equal(2, await apps.AuditEventsAsync(AuditActions.SignIn, actorId: account.Id));
            Assert.Equal(1u, (await StoredPasskeyAsync(account)).SignCount);
        }
    }

    // Step 19.1, the final review's probe with the order forced: a passkey post waits for the account while the passkey's
    // stored count goes up. Held, it reads the count as stored then, and is refused; the count stays. Before, it had
    // read the count before it waited, signed in, and wrote its own lower count over the stored one.
    [Fact]
    public async Task A_passkey_waiting_for_the_account_is_checked_against_its_count_as_stored_then()
    {
        var account = await apps.CreateStaffAsync("Dr. Count Raised", StaffRoles.Clinician);
        using var authenticator = new TestAuthenticator();
        using (var added = await AddAsync((await SignInAsync(apps.OnPrem, account)).Client, account, authenticator))
        {
            Assert.Contains("Passkey added:", Words(await ReadAsync(added)));
        }

        var (client, codePage) = await PasswordAsync(apps.OnPrem, account);
        using var optionsResponse = await OptionsToSignInAsync(client, codePage);
        var (options, state) = await ReadyAsync(optionsResponse);
        var credential = authenticator.Get(options, WebApps.OnSiteOrigin);

        Task<HttpResponseMessage> posting;
        await using (var hold = await HoldAccountAsync(apps, account.Id))
        {
            posting = PasskeyAsync(client, codePage, credential, state);
            await WaitingForLocksAsync(apps, 1);
            // As another sign-in with the same passkey would leave it, far past this answer's count of 1.
            await using (var raise = new Npgsql.NpgsqlCommand(
                "UPDATE ehr.staff_user_passkeys SET data = jsonb_set(data, '{SignCount}', '50') WHERE user_id = @id", hold.Connection, hold.Transaction))
            {
                raise.Parameters.AddWithValue("id", account.Id);
                Assert.Equal(1, await raise.ExecuteNonQueryAsync(Cancel));
            }

            await hold.Transaction.CommitAsync(Cancel);
        }

        using var refused = await posting;

        Assert.Equal(CodePage, PathOf(refused));
        Assert.Contains("That passkey didn't work.", Words(await ReadAsync(refused)));
        Assert.Equal(50u, (await StoredPasskeyAsync(account)).SignCount);
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.SignIn, actorId: account.Id));
        Assert.Equal(new[] { "passkey refused" }, await ReasonsAsync(account));
    }

    // Step 19.1, found by reading: the old app's code, or an old recovery code, posted as an administrator's
    // authenticator reset commits. The post decided the account's next step, reading its authenticator key, before the
    // reset, and holds the account after it: it is sent back to the password before any code is checked, and nothing
    // signs in, then or after the next account check. Before, the app's code was checked against the key read before the
    // reset, and signed in under the security stamp the reset had just set, so the session outlived the reset; a
    // recovery code, which the reset leaves stored, would have done the same once recovery codes signed in.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_code_posted_as_a_reset_commits_signs_nobody_in(bool recoveryCode)
    {
        var admin = await apps.CreateStaffAsync($"Resets During A Code {recoveryCode}", StaffRoles.Admin);
        var adminSession = await SignInAsync(apps.OnPrem, admin);
        var account = await apps.CreateStaffAsync($"Dr. Code During Reset {recoveryCode}", StaffRoles.Clinician);
        var code = recoveryCode ? (await apps.RecoveryCodesAsync(account))[0] : Totp.Code(account.AuthenticatorKey);
        var (client, codePage) = await PasswordAsync(apps.OnPrem, account);
        var adminToken = Token(await adminSession.Client.GetStringAsync("/admin/staff", Cancel));

        Task<HttpResponseMessage> resetting, posting;
        await using (var hold = await HoldAccountAsync(apps, account.Id))
        {
            resetting = adminSession.Client.PostAsync("/admin/staff?handler=ResetAuthenticator", Form(("id", account.Id), ("__RequestVerificationToken", adminToken)), Cancel);
            await WaitingForLocksAsync(apps, 1);
            posting = client.PostAsync(CodePage, Form(("Code", code), ("__RequestVerificationToken", Token(codePage))), Cancel);
            await WaitingForLocksAsync(apps, 2);
            await hold.Transaction.CommitAsync(Cancel);
        }

        using (var reset = await resetting)
        {
            Assert.Contains("will set up an authenticator app again at their next sign-in.", Words(await ReadAsync(reset)));
        }

        using var posted = await posting;
        var then = PathOf(posted);
        apps.Clock.Pass(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(5));
        using var later = await client.GetAsync("/patients", Cancel);

        Assert.Equal(("/sign-in", "/sign-in"), (then, PathOf(later)));
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.SignIn, actorId: account.Id));
    }

    // Step 19.1: a recovery code signs in, once, typed as the enrolment page shows it, without its dash, or in lower case
    // with spaces round it; the same code again is refused and counted as a wrong code. Before, the dash was taken out
    // as an authenticator code's is, so no recovery code ever matched the one stored: each was refused and counted
    // towards the lockout, since the staff sign-in was built (8e372bd).
    [Theory]
    [InlineData("as shown")]
    [InlineData("without its dash")]
    [InlineData("in lower case with spaces round it")]
    public async Task A_recovery_code_signs_in_once_however_it_is_typed(string typed)
    {
        var account = await apps.CreateStaffAsync($"Dr. Lost Phone, {typed}", StaffRoles.Clinician);
        var codes = await apps.RecoveryCodesAsync(account);
        var code = typed switch
        {
            "as shown" => codes[0],
            "without its dash" => codes[0].Replace("-", "", StringComparison.Ordinal),
            _ => $"  {codes[0].ToLowerInvariant()} ",
        };

        var (client, _) = await PasswordAsync(apps.OnPrem, account);
        using var signedIn = await PostFormAsync(client, CodePage, CodePage, ("Code", code));

        Assert.Equal("/patients", PathOf(signedIn));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.RecoveryCodeUsed, actorId: account.Id));
        Assert.Equal(StaffSignIn.RecoveryCodes - 1, await RecoveryCodesLeftAsync(account));

        var (again, _) = await PasswordAsync(apps.OnPrem, account);
        using var refused = await PostFormAsync(again, CodePage, CodePage, ("Code", code));

        Assert.Equal(CodePage, PathOf(refused));
        Assert.Contains("That code did not match.", await ReadAsync(refused));
        Assert.Equal(new[] { "wrong code" }, await ReasonsAsync(account));
        Assert.Equal(1, (await AccountOfAsync(account)).AccessFailedCount);
    }

    // A wrong code of a recovery code's shape is refused and counts towards the lockout, as any wrong code does.
    [Fact]
    public async Task A_wrong_code_of_a_recovery_codes_shape_counts_towards_the_lockout()
    {
        var account = await apps.CreateStaffAsync("Dr. Wrong Recovery", StaffRoles.Clinician);
        var codes = await apps.RecoveryCodesAsync(account);
        // A recovery code's shape, in Identity's alphabet for them, and none of the account's.
        const string wrong = "22222-22222";
        Assert.DoesNotContain(wrong, codes);

        var (client, _) = await PasswordAsync(apps.OnPrem, account);
        using var refused = await PostFormAsync(client, CodePage, CodePage, ("Code", wrong));

        Assert.Contains("That code did not match.", await ReadAsync(refused));
        Assert.Equal(new[] { "wrong code" }, await ReasonsAsync(account));
        Assert.Equal(1, (await AccountOfAsync(account)).AccessFailedCount);
        Assert.Equal(StaffSignIn.RecoveryCodes, await RecoveryCodesLeftAsync(account));
    }

    // Step 19.1's final review: an administrator reissues the password between the password step and the second factor,
    // at once or with the second factor's post waiting for the account behind the reissue. The pending sign-in carries
    // the account's security stamp from its password step, which the reissue changes: the code, a recovery code and a
    // passkey each go back to the password, nothing counted and no code used up, and nothing is signed in, then or after
    // the next account check. Before, each signed in under the stamp the reissue had just set, and the session outlived
    // the reissue meant to end it, since the staff sign-in was built.
    [Theory]
    [InlineData("code", false)]
    [InlineData("recovery code", false)]
    [InlineData("passkey", false)]
    [InlineData("code", true)]
    [InlineData("recovery code", true)]
    [InlineData("passkey", true)]
    public async Task A_password_reissued_after_the_password_step_ends_the_sign_in(string factor, bool waiting)
    {
        var admin = await apps.CreateStaffAsync($"Reissues {factor} {waiting}", StaffRoles.Admin);
        var adminSession = await SignInAsync(apps.OnPrem, admin);
        var account = await apps.CreateStaffAsync($"Dr. Password Reissued, {factor} {waiting}", StaffRoles.Clinician);
        using var authenticator = new TestAuthenticator();
        if (factor == "passkey")
        {
            using var added = await AddAsync((await SignInAsync(apps.OnPrem, account)).Client, account, authenticator);
            Assert.Contains("Passkey added:", Words(await ReadAsync(added)));
        }

        var recovery = (await apps.RecoveryCodesAsync(account))[0];
        var (client, codePage) = await PasswordAsync(apps.OnPrem, account);
        (string, object)[] fields = factor switch
        {
            "code" => [("Code", Totp.Code(account.AuthenticatorKey))],
            "recovery code" => [("Code", recovery)],
            _ => await PasskeyAnswerAsync(client, codePage, authenticator),
        };
        var url = factor == "passkey" ? $"{CodePage}?handler=Passkey" : CodePage;
        var signInsBefore = await apps.AuditEventsAsync(AuditActions.SignIn, actorId: account.Id);
        var adminToken = Token(await adminSession.Client.GetStringAsync("/admin/staff", Cancel));
        Task<HttpResponseMessage> Reissue() => adminSession.Client.PostAsync(
            "/admin/staff?handler=Reissue", Form(("id", account.Id), ("__RequestVerificationToken", adminToken)), Cancel);
        Task<HttpResponseMessage> Post() => client.PostAsync(url, Form([.. fields, ("__RequestVerificationToken", Token(codePage))]), Cancel);

        HttpResponseMessage reissued, posted;
        if (waiting)
        {
            Task<HttpResponseMessage> reissuing, posting;
            await using (var hold = await HoldAccountAsync(apps, account.Id))
            {
                reissuing = Reissue();
                await WaitingForLocksAsync(apps, 1);
                posting = Post();
                await WaitingForLocksAsync(apps, 2);
                await hold.Transaction.CommitAsync(Cancel);
            }

            (reissued, posted) = (await reissuing, await posting);
        }
        else
        {
            reissued = await Reissue();
            posted = await Post();
        }

        Assert.Contains("/sign-in/setup?user=", await ReadAsync(reissued));
        var then = PathOf(posted);
        apps.Clock.Pass(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(5));
        using var later = await client.GetAsync("/patients", Cancel);

        Assert.Equal(("/sign-in", "/sign-in"), (then, PathOf(later)));
        Assert.Equal(signInsBefore, await apps.AuditEventsAsync(AuditActions.SignIn, actorId: account.Id));
        Assert.Empty(await ReasonsAsync(account));
        Assert.Equal(0, (await AccountOfAsync(account)).AccessFailedCount);
        Assert.Equal(StaffSignIn.RecoveryCodes, await RecoveryCodesLeftAsync(account));
        reissued.Dispose();
        posted.Dispose();
    }

    // Step 19.1's final review: the same at the enrolment an account owes, its password step before the reissue: the
    // enrolment, with the key it was shown, goes back to the password, and the password it had is refused. Before, the
    // enrolment completed and signed in.
    [Fact]
    public async Task A_password_reissued_after_the_password_step_ends_an_enrolment_too()
    {
        var admin = await apps.CreateStaffAsync("Reissues During Enrolment", StaffRoles.Admin);
        var adminSession = await SignInAsync(apps.OnPrem, admin);
        var account = await apps.CreateStaffWithoutAuthenticatorAsync("Dr. Enrols After Reissue", StaffRoles.Clinician);
        var client = Client(apps.OnPrem, new CookieContainer());
        using var enrollPage = await PostFormAsync(client, "/sign-in", "/sign-in", ("Input.Email", account.Email), ("Input.Password", account.Password));
        Assert.Equal("/sign-in/enroll", PathOf(enrollPage));
        var key = AuthenticatorSecret().Match(await enrollPage.Content.ReadAsStringAsync(Cancel)).Groups[1].Value;

        using (var reissued = await PostFormAsync(adminSession.Client, "/admin/staff?handler=Reissue", "/admin/staff", ("id", account.Id)))
        {
            Assert.Contains("/sign-in/setup?user=", await ReadAsync(reissued));
        }

        using var enrolled = await PostFormAsync(client, "/sign-in/enroll", enrollPage, ("Code", Totp.Code(key)));
        using var later = await client.GetAsync("/patients", Cancel);
        using var again = await PostFormAsync(Client(apps.OnPrem, new CookieContainer()), "/sign-in", "/sign-in", ("Input.Email", account.Email), ("Input.Password", account.Password));

        Assert.Equal(("/sign-in", "/sign-in"), (PathOf(enrolled), PathOf(later)));
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.SignIn, actorId: account.Id));
        Assert.Contains("Wrong email or password.", await ReadAsync(again));
    }

    // A pending sign-in written before step 19.1 carries no security stamp: it goes back to the password, never a 500.
    [Fact]
    public async Task A_pending_sign_in_from_before_the_stamp_was_kept_goes_back_to_the_password()
    {
        var account = await apps.CreateStaffAsync("Dr. Old Pending Cookie", StaffRoles.Clinician);
        var options = apps.OnPrem.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AccountSchemes.StaffPending);
        var now = apps.Clock.GetUtcNow();
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, account.Id.ToString())], AccountSchemes.StaffPending)),
            new AuthenticationProperties(new Dictionary<string, string?> { ["ehr:pending"] = "00" }) { IssuedUtc = now, ExpiresUtc = now.AddMinutes(10) },
            AccountSchemes.StaffPending);
        var jar = new CookieContainer();
        jar.Add(Base, new Cookie("__Host-Ehr.StaffPending", options.TicketDataFormat.Protect(ticket), "/") { Secure = true });
        var client = Client(apps.OnPrem, jar);

        using var page = await client.GetAsync(CodePage, Cancel);
        var token = Token(await client.GetStringAsync("/sign-in", Cancel));
        using var posted = await client.PostAsync(CodePage, Form(("Code", Totp.Code(account.AuthenticatorKey)), ("__RequestVerificationToken", token)), Cancel);

        Assert.Equal(("/sign-in", HttpStatusCode.OK), (PathOf(page), page.StatusCode));
        Assert.Equal(("/sign-in", HttpStatusCode.OK), (PathOf(posted), posted.StatusCode));
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.SignIn, actorId: account.Id));
    }

    const string LockedWords = "This account is locked after too many failed attempts. Try again in 15 minutes.";

    // Step 19.2: ten wrong passwords sent at once for one account, ten times over. Each password is checked, then the
    // account held and read again before anything is counted: five are counted and recorded as wrong, the fifth locking
    // the account and answered as locked, as it always was; the other five are refused and recorded as locked, never a
    // 500; and then the right password is refused as locked. Before, nine of ten answered 500, one was counted and
    // recorded, and the account wasn't locked.
    [Fact]
    public async Task Wrong_passwords_sent_at_once_are_each_counted_and_the_fifth_locks_the_account()
    {
        for (var run = 0; run < 10; run++)
        {
            var account = await apps.CreateStaffAsync($"Dr. Passwords At Once {run}", StaffRoles.Clinician);

            var answers = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => PasswordStepAsync(account, "not-the-password-at-all")));

            Assert.True(answers.All(a => a.Status == HttpStatusCode.OK), $"Run {run}: {string.Join(", ", answers.Select(a => (int)a.Status))}.");
            Assert.Equal((4, 6), (answers.Count(a => a.Words.Contains("Wrong email or password.", StringComparison.Ordinal)), answers.Count(a => a.Words.Contains(LockedWords, StringComparison.Ordinal))));
            var reasons = await ReasonsAsync(account);
            Assert.Equal((5, 5), (reasons.Count(r => r == "wrong password"), reasons.Count(r => r == "locked out")));
            var right = await PasswordStepAsync(account, account.Password);
            Assert.Equal("/sign-in", right.Path);
            Assert.Contains(LockedWords, right.Words);
        }
    }

    // Step 19.2: the right password among nine wrong ones, all at once, ten times over. It is checked in its turn: it goes
    // on to the code if its turn comes before the fifth wrong one, or is refused as locked; never a 500, and each
    // refusal is recorded once. (A right password still resets the count of wrong ones, as it always has: step 19's open
    // point.) Before, most of them answered 500.
    [Fact]
    public async Task A_right_password_among_wrong_ones_at_once_is_answered_in_its_turn()
    {
        for (var run = 0; run < 10; run++)
        {
            var account = await apps.CreateStaffAsync($"Dr. Right Among Wrong {run}", StaffRoles.Clinician);

            var answers = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => PasswordStepAsync(account, i == 4 ? account.Password : "not-the-password-at-all")));

            Assert.True(answers.All(a => a.Status == HttpStatusCode.OK), $"Run {run}: {string.Join(", ", answers.Select(a => (int)a.Status))}.");
            Assert.True(answers[4].Path == CodePage || answers[4].Words.Contains(LockedWords, StringComparison.Ordinal), $"Run {run}: the right password at {answers[4].Path}.");
            var refused = answers.Where(a => a.Path == "/sign-in").ToList();
            Assert.Equal(answers.Length - (answers[4].Path == CodePage ? 1 : 0), refused.Count);
            Assert.All(refused, a => Assert.True(a.Words.Contains("Wrong email or password.", StringComparison.Ordinal) || a.Words.Contains(LockedWords, StringComparison.Ordinal)));
            Assert.Equal(refused.Count, (await ReasonsAsync(account)).Count);
        }
    }

    // Step 19.2: the right password, its hash checked, then waiting for the account behind an administrator's password
    // reissue, or ahead of it. Behind it, the password checked is no longer the account's: it is answered as a wrong
    // one, and nothing is counted or recorded. Ahead of it, it goes on to the code, and the reissue then sends the code
    // back to the password (step 19.1). Before, behind the reissue, it went on to the code all the same.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_right_password_racing_a_reissue_never_outlives_it(bool reissueFirst)
    {
        var admin = await apps.CreateStaffAsync($"Reissues During Password {reissueFirst}", StaffRoles.Admin);
        var adminSession = await SignInAsync(apps.OnPrem, admin);
        var account = await apps.CreateStaffAsync($"Dr. Password During Reissue {reissueFirst}", StaffRoles.Clinician);
        // A wrong password first, so the account has a count for the right one to reset (and, before, a write to wait on).
        Assert.Contains("Wrong email or password.", (await PasswordStepAsync(account, "not-the-password-at-all")).Words);
        var jar = new CookieContainer();
        var client = Client(apps.OnPrem, jar);
        var signInToken = Token(await client.GetStringAsync("/sign-in", Cancel));
        var adminToken = Token(await adminSession.Client.GetStringAsync("/admin/staff", Cancel));
        Task<HttpResponseMessage> Password() => client.PostAsync(
            "/sign-in", Form(("Input.Email", account.Email), ("Input.Password", account.Password), ("__RequestVerificationToken", signInToken)), Cancel);
        Task<HttpResponseMessage> Reissue() => adminSession.Client.PostAsync(
            "/admin/staff?handler=Reissue", Form(("id", account.Id), ("__RequestVerificationToken", adminToken)), Cancel);

        Task<HttpResponseMessage> stepping, reissuing;
        await using (var hold = await HoldAccountAsync(apps, account.Id))
        {
            if (reissueFirst)
            {
                reissuing = Reissue();
                await WaitingForLocksAsync(apps, 1);
                stepping = Password();
            }
            else
            {
                stepping = Password();
                await WaitingForLocksAsync(apps, 1);
                reissuing = Reissue();
            }

            await WaitingForLocksAsync(apps, 2);
            await hold.Transaction.CommitAsync(Cancel);
        }

        using var step = await stepping;
        using var reissued = await reissuing;
        Assert.Contains("/sign-in/setup?user=", await ReadAsync(reissued));
        if (reissueFirst)
        {
            Assert.Equal("/sign-in", PathOf(step));
            Assert.Contains("Wrong email or password.", Words(await ReadAsync(step)));
            Assert.Equal(new[] { "wrong password" }, await ReasonsAsync(account));
            Assert.Equal(1, (await AccountOfAsync(account)).AccessFailedCount);
        }
        else
        {
            Assert.Equal(CodePage, PathOf(step));
            using var code = await PostFormAsync(client, CodePage, step, ("Code", Totp.Code(account.AuthenticatorKey)));
            Assert.Equal("/sign-in", PathOf(code));
        }

        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.SignIn, actorId: account.Id));
    }

    // A password step from a browser of its own: its status, where it lands, and what the page says.
    async Task<(HttpStatusCode Status, string Path, string Words)> PasswordStepAsync(StaffAccount account, string password)
    {
        var client = Client(apps.OnPrem, new CookieContainer());
        using var response = await PostFormAsync(client, "/sign-in", "/sign-in", ("Input.Email", account.Email), ("Input.Password", password));
        return (response.StatusCode, PathOf(response), Words(await response.Content.ReadAsStringAsync(Cancel)));
    }

    // The fields of a passkey's answer for this password step, as passkeys.js sends them.
    static async Task<(string, object)[]> PasskeyAnswerAsync(HttpClient client, string codePage, TestAuthenticator authenticator)
    {
        using var optionsResponse = await OptionsToSignInAsync(client, codePage);
        var (options, state) = await ReadyAsync(optionsResponse);
        return [("credential", authenticator.Get(options, WebApps.OnSiteOrigin)), ("state", state)];
    }

    async Task<StaffUser> AccountOfAsync(StaffAccount account)
    {
        await using var scope = apps.OnPrem.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>().FindByIdAsync(account.Id.ToString()))!;
    }

    async Task<int> RecoveryCodesLeftAsync(StaffAccount account)
    {
        await using var scope = apps.OnPrem.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        return await users.CountRecoveryCodesAsync((await users.FindByIdAsync(account.Id.ToString()))!);
    }

    async Task<UserPasskeyInfo> StoredPasskeyAsync(StaffAccount account)
    {
        await using var scope = apps.OnPrem.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        return Assert.Single(await users.GetPasskeysAsync((await users.FindByIdAsync(account.Id.ToString()))!));
    }

    // Why the account's sign-ins failed, as the audit log says.
    async Task<List<string>> ReasonsAsync(StaffAccount account)
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT reason FROM ehr.audit_events WHERE action = @action AND actor_id = @actor ORDER BY occurred_at, id", connection);
        command.Parameters.AddWithValue("action", AuditActions.SignInFailed);
        command.Parameters.AddWithValue("actor", account.Id);
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        var reasons = new List<string>();
        while (await reader.ReadAsync(Cancel))
        {
            reasons.Add(reader.GetString(0));
        }

        return reasons;
    }

    [Fact]
    public async Task The_password_alone_signs_nobody_in()
    {
        var account = await apps.CreateStaffAsync("Dr. Halfway", StaffRoles.Clinician);
        var jar = new CookieContainer();
        var client = Client(apps.OnPrem, jar);

        using var codePage = await PostFormAsync(client, "/sign-in", "/sign-in", ("Input.Email", account.Email), ("Input.Password", account.Password));
        Assert.Equal("/sign-in/code", PathOf(codePage));

        using var home = await Client(apps.OnPrem, jar, followRedirects: false).GetAsync("/", Cancel);
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.SignIn, actorId: account.Id));
    }

    [Fact]
    public async Task A_wrong_code_is_refused()
    {
        var account = await apps.CreateStaffAsync("Dr. Mistyped", StaffRoles.Clinician);
        var client = Client(apps.OnPrem, new CookieContainer());
        using var codePage = await PostFormAsync(client, "/sign-in", "/sign-in", ("Input.Email", account.Email), ("Input.Password", account.Password));

        using var response = await PostFormAsync(client, "/sign-in/code", codePage, ("Code", Totp.WrongCode(account.AuthenticatorKey)));

        Assert.Contains("That code did not match", await ReadAsync(response));
        Assert.Equal("/sign-in/code", PathOf(response));
    }

    [Fact]
    public async Task The_authenticator_code_signs_in_and_the_sign_in_is_recorded()
    {
        var account = await apps.CreateStaffAsync("Dr. Signed In", StaffRoles.Clinician);

        var session = await SignInAsync(apps.OnPrem, account);

        var html = await session.Client.GetStringAsync("/", Cancel);
        Assert.Contains("Dr. Signed In", html);
        Assert.Contains("My patients", html);
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.SignIn, actorId: account.Id));
    }

    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        var account = await apps.CreateStaffAsync("Dr. Leaving", StaffRoles.Clinician);
        var session = await SignInAsync(apps.OnPrem, account);

        using var signedOut = await PostFormAsync(session.Client, "/sign-out", "/");
        Assert.Contains("Signed out", await ReadAsync(signedOut));

        using var home = await Client(apps.OnPrem, session.Jar, followRedirects: false).GetAsync("/", Cancel);
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.SignOut, actorId: account.Id));
    }

    [Fact]
    public async Task The_bootstrap_admin_changes_its_password_and_enrolls_an_authenticator_at_first_sign_in()
    {
        var jar = new CookieContainer();
        var client = Client(apps.OnPrem, jar);

        // The password from configuration gets the account only as far as choosing its own.
        using var passwordPage = await PostFormAsync(client, "/sign-in", "/sign-in",
            ("Input.Email", WebApps.BootstrapAdminEmail), ("Input.Password", WebApps.BootstrapAdminPassword));
        Assert.Equal("/sign-in/password", PathOf(passwordPage));

        const string newPassword = "the-admin-chose-this-one";
        using var enrollPage = await PostFormAsync(client, "/sign-in/password", passwordPage, ("Input.Password", newPassword), ("Input.Confirm", newPassword));
        Assert.Equal("/sign-in/enroll", PathOf(enrollPage));

        // The key the page shows is what the phone would scan.
        var enrollHtml = await enrollPage.Content.ReadAsStringAsync(Cancel);
        var key = AuthenticatorSecret().Match(enrollHtml).Groups[1].Value;
        Assert.Equal(32, key.Length);

        using var recoveryPage = await PostFormAsync(client, "/sign-in/enroll", enrollPage, ("Code", Totp.Code(key)));
        var recoveryHtml = await ReadAsync(recoveryPage);
        Assert.Contains("Your recovery codes", recoveryHtml);
        Assert.Equal(8, RecoveryCode().Matches(recoveryHtml).Count);

        // Signed in, as an admin: the staff page, not the patients.
        using var staff = await Client(apps.OnPrem, jar, followRedirects: false).GetAsync("/admin/staff", Cancel);
        Assert.Equal(HttpStatusCode.OK, staff.StatusCode);
        using var patients = await Client(apps.OnPrem, jar, followRedirects: false).GetAsync("/patients", Cancel);
        Assert.Equal("/denied", new Uri(Base, patients.Headers.Location!).AbsolutePath);

        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.BootstrapAdmin));
        // The admin's own: other tests in this class enroll accounts too.
        await using var scope = apps.OnPrem.Services.CreateAsyncScope();
        var admin = await scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>().FindByEmailAsync(WebApps.BootstrapAdminEmail);
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.MfaEnrolled, actorId: admin!.Id));
    }

    // Off-site, the cloud app signs the person out itself and queues the event, so it works however the on-prem app
    // is down.
    [Theory]
    [InlineData("link down")]
    [InlineData("primary down")]
    public async Task Off_site_signing_out_works_while_the_on_prem_app_cant_save(string outage)
    {
        var account = await apps.CreateStaffAsync("Dr. Leaving", StaffRoles.Clinician);
        var app = outage == "link down" ? apps.CloudCutOff : apps.CloudPrimaryDrill;
        var session = await SignInAsync(app, account, via: apps.Cloud);
        apps.PrimaryDown = outage == "primary down";
        try
        {
            Assert.False(await app.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));

            using var signedOut = await PostFormAsync(session.Client, "/sign-out", QuickTexts);

            Assert.Contains("Signed out", await ReadAsync(signedOut));
            // The replica has no WAL position of its own, and asking for one would fail.
            Assert.False(signedOut.Headers.Contains(DatabaseTransactionFilter.WalPositionHeader));
            using var home = await Client(app, session.Jar, followRedirects: false).GetAsync("/", Cancel);
            Assert.Equal("/sign-in", new Uri(Base, home.Headers.Location!).AbsolutePath);
            var (queued, _) = app.Services.GetRequiredService<CloudAuditQueue>().ReadPending(int.MaxValue);
            Assert.Contains(queued, e => e.Action == AuditActions.SignOut && e.ActorId == account.Id && e.PatientId is null && e.TableName is null);
        }
        finally
        {
            apps.PrimaryDown = false;
        }
    }

    [Fact]
    public async Task An_admin_invites_a_clinician_who_sets_a_password_and_enrolls()
    {
        var admin = await apps.CreateStaffAsync("The Admin", StaffRoles.Admin);
        var adminSession = await SignInAsync(apps.OnPrem, admin);
        var email = WebApps.NewEmail();

        using var invited = await PostFormAsync(adminSession.Client, "/admin/staff", "/admin/staff",
            ("Input.Email", email), ("Input.DisplayName", "Dr. Invited"), ("Input.Clinician", "true"), ("Input.Admin", "false"));
        var invitedHtml = await ReadAsync(invited);
        var setupLink = WebUtility.HtmlDecode(SetupLink().Match(invitedHtml).Value);
        Assert.StartsWith("https://localhost/sign-in/setup?user=", setupLink);
        Assert.Contains("Invited, not set up", invitedHtml);

        // The invited person, in a browser of their own.
        var jar = new CookieContainer();
        var client = Client(apps.OnPrem, jar);
        var setupPath = new Uri(setupLink).PathAndQuery;
        const string password = "chosen-by-the-clinician";
        using var afterSetup = await PostFormAsync(client, setupPath, setupPath, ("Input.Password", password), ("Input.Confirm", password));
        Assert.Equal("/sign-in", PathOf(afterSetup));
        Assert.Contains("Your password is set.", await ReadAsync(afterSetup));

        // The link is spent.
        using var again = await client.GetAsync(setupPath, Cancel);
        Assert.Contains("is not valid or has already been used", await ReadAsync(again));

        using var enrollPage = await PostFormAsync(client, "/sign-in", afterSetup, ("Input.Email", email), ("Input.Password", password));
        Assert.Equal("/sign-in/enroll", PathOf(enrollPage));
        var key = AuthenticatorSecret().Match(await enrollPage.Content.ReadAsStringAsync(Cancel)).Groups[1].Value;
        using var recoveryPage = await PostFormAsync(client, "/sign-in/enroll", enrollPage, ("Code", Totp.Code(key)));
        Assert.Contains("Your recovery codes", await ReadAsync(recoveryPage));

        using var patients = await Client(apps.OnPrem, jar, followRedirects: false).GetAsync("/patients", Cancel);
        Assert.Equal(HttpStatusCode.OK, patients.StatusCode);
    }

    [GeneratedRegex("secret=([A-Z2-7]+)")]
    private static partial Regex AuthenticatorSecret();

    [GeneratedRegex("<li class=\"mono-code\">")]
    private static partial Regex RecoveryCode();

    [GeneratedRegex("https://[^<\\s]+/sign-in/setup\\?user=[^<\\s]+")]
    private static partial Regex SetupLink();
}
