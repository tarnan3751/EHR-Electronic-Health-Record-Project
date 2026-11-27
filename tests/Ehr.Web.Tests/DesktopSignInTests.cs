using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Domain.Accounts;
using Ehr.Testing;
using Ehr.Web.Api.Desktop;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.PasskeyFlows;

namespace Ehr.Web.Tests;

// The desktop app's sign-in (Api/Desktop, DesktopSessions, DesktopTokenHandler): both factors for a token, the token on
// the sync API on both servers, and the ways a session ends.
public class DesktopSignInTests(WebApps apps) : IClassFixture<WebApps>
{
    const string Sync = "/api/sync/quick-texts?since=0";

    [Fact]
    public async Task A_clinician_signs_in_with_both_factors_and_syncs_on_both_servers()
    {
        var account = await apps.CreateStaffAsync("Dr. Desktop", StaffRoles.Clinician);

        var token = await apps.DesktopSignInAsync(apps.OnPrem, account);

        using var onPrem = await GetAsync(apps.OnPrem, Sync, token);
        using var cloud = await GetAsync(apps.Cloud, Sync, token);
        Assert.Equal(HttpStatusCode.OK, onPrem.StatusCode);
        Assert.Equal(HttpStatusCode.OK, cloud.StatusCode);
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.SignIn, actorId: account.Id));
    }

    // Step 19.1: the desktop app's sign-in takes a recovery code in place of the authenticator's code, as its page says,
    // once. Before, no recovery code ever signed in, here or on the web (StaffSignIn).
    [Fact]
    public async Task A_recovery_code_signs_the_desktop_app_in_once()
    {
        var account = await apps.CreateStaffAsync("Dr. Desktop Recovery", StaffRoles.Clinician);
        var code = (await apps.RecoveryCodesAsync(account))[0];

        using var signedIn = await apps.DesktopSignInResponseAsync(apps.OnPrem, account, code);
        using var again = await apps.DesktopSignInResponseAsync(apps.OnPrem, account, code);

        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.RecoveryCodeUsed, actorId: account.Id));
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.SignIn, actorId: account.Id));
    }

    // Step 19.1's final review: a desktop sign-in waiting behind an administrator's authenticator reset, with the old
    // app's code or a recovery code. Held and read again once its password is checked, the account has changed since:
    // refused with a 401, nothing counted, no recovery code used up. Before, the sign-in read the account before the
    // reset and wrote after it: with a recovery code its failure was written over a stale account, and it answered 500;
    // with the app's code it answered 200 with a token the reset had already ended.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_desktop_sign_in_waiting_behind_a_reset_is_refused(bool recoveryCode)
    {
        var admin = await apps.CreateStaffAsync($"Desktop Reset Admin {recoveryCode}", StaffRoles.Admin);
        var adminSession = await SignInAsync(apps.OnPrem, admin);
        var account = await apps.CreateStaffAsync($"Dr. Desktop During Reset {recoveryCode}", StaffRoles.Clinician);
        var code = recoveryCode ? (await apps.RecoveryCodesAsync(account))[0] : Totp.Code(account.AuthenticatorKey);
        var adminToken = Token(await adminSession.Client.GetStringAsync("/admin/staff", Cancel));

        Task<HttpResponseMessage> resetting, signingIn;
        await using (var hold = await HoldAccountAsync(apps, account.Id))
        {
            resetting = adminSession.Client.PostAsync("/admin/staff?handler=ResetAuthenticator", Form(("id", account.Id), ("__RequestVerificationToken", adminToken)), Cancel);
            await WaitingForLocksAsync(apps, 1);
            signingIn = apps.DesktopSignInResponseAsync(apps.OnPrem, account, code);
            await WaitingForLocksAsync(apps, 2);
            await hold.Transaction.CommitAsync(Cancel);
        }

        using var reset = await resetting;
        using var signedIn = await signingIn;

        Assert.Contains("Authenticator reset for ", Words(await ReadAsync(reset)));
        Assert.Equal(HttpStatusCode.Unauthorized, signedIn.StatusCode);
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.SignIn, actorId: account.Id));
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.SignInFailed, actorId: account.Id));
        var (failed, codesLeft) = await FailedAndCodesLeftAsync(account);
        Assert.Equal((0, StaffSignIn.RecoveryCodes), (failed, recoveryCode ? codesLeft : StaffSignIn.RecoveryCodes));
    }

    // Step 19.1's final review: one recovery code sent twice at once from the desktop app, the second waiting behind the
    // first, and then at once ten times: one signs in, the other is refused as the used code it then is, never a 500.
    // Before, the second wrote its failure over the account as it had read it before the first's redemption, and
    // answered 500.
    [Fact]
    public async Task One_recovery_code_sent_twice_at_once_signs_the_desktop_app_in_once()
    {
        for (var run = 0; run < 11; run++)
        {
            var account = await apps.CreateStaffAsync($"Dr. Desktop Code Twice {run}", StaffRoles.Clinician);
            var code = (await apps.RecoveryCodesAsync(account))[0];
            HttpResponseMessage[] answers;
            if (run == 0)
            {
                Task<HttpResponseMessage> first, second;
                await using (var hold = await HoldAccountAsync(apps, account.Id))
                {
                    first = apps.DesktopSignInResponseAsync(apps.OnPrem, account, code);
                    await WaitingForLocksAsync(apps, 1);
                    second = apps.DesktopSignInResponseAsync(apps.OnPrem, account, code);
                    await WaitingForLocksAsync(apps, 2);
                    await hold.Transaction.CommitAsync(Cancel);
                }

                answers = [await first, await second];
            }
            else
            {
                answers = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => apps.DesktopSignInResponseAsync(apps.OnPrem, account, code)));
            }

            var statuses = answers.Select(a => a.StatusCode).Order().ToList();
            Array.ForEach(answers, a => a.Dispose());
            Assert.True(statuses.SequenceEqual([HttpStatusCode.OK, HttpStatusCode.Unauthorized]), $"Run {run}: {string.Join(", ", statuses.Select(s => (int)s))}.");
            Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.RecoveryCodeUsed, actorId: account.Id));
            Assert.Equal((1, StaffSignIn.RecoveryCodes - 1), await FailedAndCodesLeftAsync(account));
        }
    }

    const string LockedProblem = "Account locked. Try again in 15 minutes.";

    // Step 19.2: ten wrong passwords sent at once from the desktop app for one account, ten times over, as at the web:
    // five counted and recorded as wrong, the fifth locking the account, the rest refused and recorded as locked, every
    // one a 401 and none a 500; then the right password and code are refused as locked. Before, nine of ten answered 500,
    // one was counted, and the account wasn't locked.
    [Fact]
    public async Task Wrong_passwords_sent_at_once_from_the_desktop_app_are_each_counted()
    {
        for (var run = 0; run < 10; run++)
        {
            var account = await apps.CreateStaffAsync($"Dr. Desktop Passwords At Once {run}", StaffRoles.Clinician);

            var answers = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
            {
                using var response = await apps.DesktopSignInResponseAsync(apps.OnPrem, account with { Password = "not-the-password-at-all" }, Totp.Code(account.AuthenticatorKey));
                return (response.StatusCode, Body: await response.Content.ReadAsStringAsync(Cancel));
            }));

            Assert.True(answers.All(a => a.StatusCode == HttpStatusCode.Unauthorized), $"Run {run}: {string.Join(", ", answers.Select(a => (int)a.StatusCode))}.");
            Assert.Equal(6, answers.Count(a => a.Body.Contains(LockedProblem, StringComparison.Ordinal)));
            var reasons = await FailedReasonsAsync(account);
            Assert.Equal((5, 5), (reasons.Count(r => r == "wrong password"), reasons.Count(r => r == "locked out")));
            using var right = await apps.DesktopSignInResponseAsync(apps.OnPrem, account, Totp.Code(account.AuthenticatorKey));
            Assert.Equal(HttpStatusCode.Unauthorized, right.StatusCode);
            Assert.Contains(LockedProblem, await right.Content.ReadAsStringAsync(Cancel));
        }
    }

    // Step 19.2: a desktop sign-in whose password has been checked waits for the account while the account is locked out
    // (as wrong codes elsewhere would lock it). Held, it finds the account locked and is refused as locked before its code
    // is checked: the recovery code it sent isn't used up. Before, its code was checked all the same, and it signed in.
    [Fact]
    public async Task A_desktop_sign_in_waiting_for_an_account_locked_meanwhile_is_refused_as_locked()
    {
        var account = await apps.CreateStaffAsync("Dr. Desktop Locked Meanwhile", StaffRoles.Clinician);
        var code = (await apps.RecoveryCodesAsync(account))[0];

        Task<HttpResponseMessage> signingIn;
        await using (var hold = await HoldAccountAsync(apps, account.Id))
        {
            signingIn = apps.DesktopSignInResponseAsync(apps.OnPrem, account, code);
            await WaitingForLocksAsync(apps, 1);
            await using (var lockOut = new Npgsql.NpgsqlCommand(
                "UPDATE ehr.staff_users SET lockout_end = now() + interval '15 minutes', concurrency_stamp = gen_random_uuid()::text WHERE id = @id",
                hold.Connection, hold.Transaction))
            {
                lockOut.Parameters.AddWithValue("id", account.Id);
                Assert.Equal(1, await lockOut.ExecuteNonQueryAsync(Cancel));
            }

            await hold.Transaction.CommitAsync(Cancel);
        }

        using var signedIn = await signingIn;

        Assert.Equal(HttpStatusCode.Unauthorized, signedIn.StatusCode);
        Assert.Contains(LockedProblem, await signedIn.Content.ReadAsStringAsync(Cancel));
        Assert.Equal((0, StaffSignIn.RecoveryCodes), await FailedAndCodesLeftAsync(account));
        Assert.Equal(new[] { "locked out" }, await FailedReasonsAsync(account));
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.SignIn, actorId: account.Id));
    }

    // Step 19.2: ten wrong passwords sent at once to the desktop app's lock, which checks the password on the server
    // (step 19a), ten times over: five counted and recorded as wrong, the fifth locking the account, and the rest refused
    // as locked, recorded so, or, reaching the server once the account was locked, refused with their token; never a
    // 500. Before, most of them answered 500, and one was counted.
    [Fact]
    public async Task Wrong_passwords_sent_at_once_to_the_desktop_lock_are_each_counted()
    {
        for (var run = 0; run < 10; run++)
        {
            var account = await apps.CreateStaffAsync($"Dr. Desktop Lock At Once {run}", StaffRoles.Clinician);
            var token = await apps.DesktopSignInAsync(apps.OnPrem, account);

            var answers = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
            {
                var client = CreateClient(apps.OnPrem);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await client.PostAsJsonAsync("/api/desktop/unlock", new { password = "not-the-password-at-all" }, Cancel);
                return (response.StatusCode, Body: await response.Content.ReadAsStringAsync(Cancel));
            }));

            Assert.True(answers.All(a => a.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized), $"Run {run}: {string.Join(", ", answers.Select(a => (int)a.StatusCode))}.");
            var reasons = await FailedReasonsAsync(account);
            Assert.Equal(5, reasons.Count(r => r == "wrong password"));
            Assert.Equal(answers.Count(a => a.StatusCode == HttpStatusCode.Forbidden && a.Body.Contains(LockedProblem, StringComparison.Ordinal)) - 1, reasons.Count(r => r == "locked out"));
            using var synced = await GetAsync(apps.OnPrem, Sync, token);
            Assert.Equal(HttpStatusCode.Unauthorized, synced.StatusCode);
        }
    }

    // Why the account's sign-ins failed, as the audit log says.
    async Task<List<string>> FailedReasonsAsync(StaffAccount account)
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

    // The account's count of failed attempts and its recovery codes left.
    async Task<(int Failed, int CodesLeft)> FailedAndCodesLeftAsync(StaffAccount account)
    {
        await using var scope = apps.OnPrem.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        var user = (await users.FindByIdAsync(account.Id.ToString()))!;
        return (user.AccessFailedCount, await users.CountRecoveryCodesAsync(user));
    }

    // TC-UC7-02 (FR-21) in the SE document (Sara Lelo), server side: the offline download holds only the clinician's
    // assigned charts, so only those are available offline. The client side is store.rs
    // (an_assigned_chart_and_its_draft_reopen_without_a_connection).
    [Fact]
    public async Task Offline_download_only_includes_the_clinicians_assigned_charts()
    {
        const string offlinePanel = "/api/desktop/offline-panel";
        var clinician = await apps.CreateStaffAsync("Dr. Offline", StaffRoles.Clinician);
        var colleague = await apps.CreateStaffAsync("Dr. Colleague", StaffRoles.Clinician);
        var own = await apps.InsertPatientAsync("Demo", "Assigned");
        var other = await apps.InsertPatientAsync("Demo", "OtherPanel");
        await apps.AssignAsync(clinician, own);
        await apps.AssignAsync(colleague, other);

        var token = await apps.DesktopSignInAsync(apps.OnPrem, clinician);
        using var rejected = await PostAsync(apps.OnPrem, offlinePanel, "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        using var response = await PostAsync(apps.OnPrem, offlinePanel, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore == true);
        var charts = await response.Content.ReadFromJsonAsync<CachedChart[]>(JsonSerializerOptions.Web, Cancel);
        Assert.Equal(own, Assert.Single(charts!).Id);
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Read, actorId: clinician.Id, patientId: own));
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.Read, actorId: clinician.Id, patientId: other));
    }

    // TC-UC8-01, TC-UC8-03 and TC-UC8-05 in the SE document (Sara Lelo), server side, all in this one test:
    //   TC-UC8-01 (FR-24): a draft pushed after reconnecting is applied;
    //   TC-UC8-03 (FR-24): a pull since the watermark brings back the edit saved on the server since;
    //   TC-UC8-05 (NFR-09, FR-24): a replayed operation adds no second draft and keeps the newer one.
    [Fact]
    public async Task Draft_sync_only_returns_the_authors_drafts_and_deduplicates_an_outdated_retry()
    {
        const string push = "/api/desktop/drafts/push";
        const string pull = "/api/desktop/drafts/pull";
        var author = await apps.CreateStaffAsync("Draft author", StaffRoles.Clinician);
        var colleague = await apps.CreateStaffAsync("Draft colleague", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Demo", "Draft");
        await apps.AssignAsync(author, patient);
        await apps.AssignAsync(colleague, patient);
        var token = await apps.DesktopSignInAsync(apps.OnPrem, author);
        var otherToken = await apps.DesktopSignInAsync(apps.OnPrem, colleague);
        var id = Guid.NewGuid();
        var initial = new { Key = Guid.NewGuid(), Id = id, PatientId = patient, BaseVersion = (long?)null, Body = "First draft" };

        using var unauthorized = await PostAsync(apps.OnPrem, push, "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        var added = await PostJsonAsync(apps.OnPrem, push, token, initial);
        Assert.Equal("applied", added.GetProperty("outcome").GetString());

        var original = await PostJsonAsync(apps.Cloud, pull, token, new { Since = "0", Patients = new[] { patient } });
        Assert.Equal(id, Assert.Single(original.GetProperty("drafts").EnumerateArray()).GetProperty("id").GetGuid());
        var since = original.GetProperty("watermark").GetString();
        var changed = await PostJsonAsync(apps.Cloud, push, token, initial with { Key = Guid.NewGuid(), BaseVersion = 1, Body = "Newer copy" });
        Assert.Equal(2, changed.GetProperty("current").GetProperty("version").GetInt64());

        var retry = await PostJsonAsync(apps.OnPrem, push, token, initial);
        Assert.Equal("duplicate", retry.GetProperty("outcome").GetString());
        Assert.Equal(2, retry.GetProperty("current").GetProperty("version").GetInt64());
        var stale = await PostJsonAsync(apps.OnPrem, push, token, initial with { Key = Guid.NewGuid(), BaseVersion = 1, Body = "Stale edit" });
        Assert.Equal("conflict", stale.GetProperty("outcome").GetString());
        Assert.Equal("Newer copy", stale.GetProperty("current").GetProperty("body").GetString());

        var delta = await PostJsonAsync(apps.OnPrem, pull, token, new { Since = since, Patients = new[] { patient } });
        Assert.Equal("Newer copy", Assert.Single(delta.GetProperty("drafts").EnumerateArray()).GetProperty("body").GetString());
        var unrelated = await PostJsonAsync(apps.OnPrem, pull, otherToken, new { Since = "0", Patients = new[] { patient } });
        Assert.Empty(unrelated.GetProperty("drafts").EnumerateArray());
    }

    static async Task<JsonElement> PostJsonAsync(WebApplicationFactory<Program> app, string path, string token, object body)
    {
        using var client = CreateClient(app);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.PostAsJsonAsync(path, body, JsonSerializerOptions.Web, Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonSerializerOptions.Web, Cancel);
    }

    [Fact]
    public async Task A_bad_token_is_refused_with_401_and_no_redirect()
    {
        using var response = await GetAsync(apps.OnPrem, Sync, "not-a-token");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task A_wrong_code_is_refused_with_a_reason_and_recorded()
    {
        var account = await apps.CreateStaffAsync("Dr. Mistyped Desktop", StaffRoles.Clinician);

        using var response = await apps.DesktopSignInResponseAsync(apps.OnPrem, account, Totp.WrongCode(account.AuthenticatorKey));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Wrong email, password or code.", await ProblemAsync(response));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.SignInFailed, actorId: account.Id));
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.SignIn, actorId: account.Id));
    }

    [Fact]
    public async Task An_account_that_has_not_finished_its_web_setup_is_refused()
    {
        var account = await apps.CreateStaffWithoutAuthenticatorAsync("Dr. Not Yet", StaffRoles.Clinician);

        using var response = await apps.DesktopSignInResponseAsync(apps.OnPrem, account, "000000");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Finish setting up your account in the web app first.", await ProblemAsync(response));
    }

    [Fact]
    public async Task An_admin_cannot_use_the_desktop_app()
    {
        var admin = await apps.CreateStaffAsync("Desktop Admin", StaffRoles.Admin);

        using var response = await apps.DesktopSignInResponseAsync(apps.OnPrem, admin, Totp.Code(admin.AuthenticatorKey));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("This account can't use the desktop app.", await ProblemAsync(response));
    }

    [Fact]
    public async Task Missing_fields_are_a_validation_problem()
    {
        using var response = await CreateClient(apps.OnPrem).PostAsJsonAsync("/api/desktop/sign-in",
            new SignInRequest("", null, " ", null), JsonSerializerOptions.Web, Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        var account = await apps.CreateStaffAsync("Dr. Leaving Desktop", StaffRoles.Clinician);
        var token = await apps.DesktopSignInAsync(apps.OnPrem, account);

        using var signedOut = await PostAsync(apps.OnPrem, "/api/desktop/sign-out", token);
        Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);

        using var afterwards = await GetAsync(apps.OnPrem, Sync, token);
        using var again = await PostAsync(apps.OnPrem, "/api/desktop/sign-out", token);
        Assert.Equal(HttpStatusCode.Unauthorized, afterwards.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.SignOut, actorId: account.Id));
    }

    [Fact]
    public async Task A_password_change_ends_the_session()
    {
        var account = await apps.CreateStaffAsync("Dr. Changed Desktop", StaffRoles.Clinician);
        var token = await apps.DesktopSignInAsync(apps.OnPrem, account);
        using var before = await GetAsync(apps.OnPrem, Sync, token);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        await apps.UpdateSecurityStampAsync(account);

        using var after = await GetAsync(apps.OnPrem, Sync, token);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task The_cloud_app_forwards_the_sign_in_to_the_on_prem_app()
    {
        var account = await apps.CreateStaffAsync("Dr. Remote Desktop", StaffRoles.Clinician);

        var token = await apps.DesktopSignInAsync(apps.Cloud, account);

        using var pull = await GetAsync(apps.Cloud, Sync, token);
        Assert.Equal(HttpStatusCode.OK, pull.StatusCode);
    }

    static Task<HttpResponseMessage> GetAsync(WebApplicationFactory<Program> app, string path, string token)
    {
        var client = CreateClient(app);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.GetAsync(path, Cancel);
    }

    internal static Task<HttpResponseMessage> PostAsync(WebApplicationFactory<Program> app, string path, string token)
    {
        var client = CreateClient(app);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.PostAsync(path, null, Cancel);
    }

    static async Task<string> ProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Refusal>(JsonSerializerOptions.Web, Cancel))!.Problem;
}
