using System.Net;
using System.Text.Json.Nodes;
using Ehr.Data.Accounts;
using Ehr.Domain.Accounts;
using Ehr.Testing;
using Ehr.Web.Api.Dictation;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// How long a web sign-in lasts (the expansion plan's step 18; SessionTimes), asked of the server alone, with the cookies'
// clock moved on (ShiftedClock): renewed once a minute has passed, by any request but a check; over 15 minutes after the
// last request, on the on-site and off-site apps alike; and what the session pages answer. The page's side, the warning
// and the end, is in the browser tests (TestCases/UC11). Each test moves the fixture's clock on from wherever it is.
public class SessionTimeTests(WebApps apps) : IClassFixture<WebApps>
{
    static readonly TimeSpan JustOver = SessionTimes.Idle + TimeSpan.FromSeconds(5);

    internal const string StaffCookie = "__Host-Ehr.Staff";

    // What /session answers, and whether the answer renewed the sign-in cookie.
    internal static async Task<(JsonNode Answer, bool Renewed)> AskAsync(HttpClient client, bool check, string path = "/session")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (check)
        {
            request.Headers.Add(SessionTimes.CheckHeader, "1");
        }

        using var response = await client.SendAsync(request, Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var renewed = response.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(cookie => cookie.StartsWith(StaffCookie + "=", StringComparison.Ordinal) || cookie.StartsWith("__Host-Ehr.Patient=", StringComparison.Ordinal));
        return (JsonNode.Parse(await response.Content.ReadAsStringAsync(Cancel))!, renewed);
    }

    // Whether the session still opens a page on the app, or is sent to sign in.
    internal static async Task<bool> SignedInAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, Session session, string path = "/quick-texts", string signIn = "/sign-in")
    {
        using var response = await Client(app, session.Jar, followRedirects: false).GetAsync(path, Cancel);
        if (response.StatusCode == HttpStatusCode.Redirect)
        {
            Assert.Equal(signIn, new Uri(Base, response.Headers.Location!).AbsolutePath);
            return false;
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return true;
    }

    [Fact]
    public async Task A_request_renews_the_session_once_a_minute_has_passed_and_a_check_never_does()
    {
        var session = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Dr. Renewed", StaffRoles.Clinician));

        var (first, renewedAtOnce) = await AskAsync(session.Client, check: false);
        Assert.False(renewedAtOnce);
        Assert.InRange((int)first["left"]!, 890, 900);
        Assert.False((bool)first["final"]!);

        apps.Clock.Pass(TimeSpan.FromMinutes(2));
        var (checkedOnly, renewedByCheck) = await AskAsync(session.Client, check: true);
        Assert.False(renewedByCheck);
        Assert.InRange((int)checkedOnly["left"]!, 770, 780);

        var (kept, renewedByRequest) = await AskAsync(session.Client, check: false);
        Assert.True(renewedByRequest);
        Assert.Equal(900, (int)kept["left"]!);
    }

    // The session's cookie made as a sign-in before step 18 wrote it: a ticket of the old 30-minute life, with neither
    // the sign-in's time nor the account check's.
    static void MakeOldShaped(WebApps apps, Session session)
    {
        var format = apps.OnPrem.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AccountSchemes.Staff).TicketDataFormat;
        var cookie = session.Jar.GetCookies(Base)[StaffCookie]!;
        var ticket = format.Unprotect(cookie.Value)!;
        ticket.Properties.Items.Remove("ehr:signed-in");
        ticket.Properties.Items.Remove("ehr:account-checked");
        var issued = apps.Clock.GetUtcNow();
        ticket.Properties.IssuedUtc = issued;
        ticket.Properties.ExpiresUtc = issued + TimeSpan.FromMinutes(30);
        session.Jar.Add(Base, new System.Net.Cookie(StaffCookie, format.Protect(ticket), "/") { Secure = true, HttpOnly = true });
    }

    // A cookie from before step 18 takes the new rules at its first request: the idle time, not its own 30 minutes, and an
    // account check at once.
    [Fact]
    public async Task A_cookie_from_before_step_18_idles_out_at_15_minutes_and_has_its_account_checked_at_once()
    {
        var session = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Dr. Old Cookie", StaffRoles.Clinician));
        MakeOldShaped(apps, session);
        apps.Clock.Pass(TimeSpan.FromMinutes(1));
        Assert.True(await SignedInAsync(apps.OnPrem, session));
        apps.Clock.Pass(JustOver);
        Assert.False(await SignedInAsync(apps.OnPrem, session));

        var disabledAccount = await apps.CreateStaffAsync("Dr. Old Cookie Disabled", StaffRoles.Clinician);
        var disabled = await SignInAsync(apps.OnPrem, disabledAccount);
        MakeOldShaped(apps, disabled);
        await using (var scope = apps.OnPrem.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
            var user = (await users.FindByIdAsync(disabledAccount.Id.ToString()))!;
            Assert.True((await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddYears(100))).Succeeded);
        }

        apps.Clock.Pass(TimeSpan.FromSeconds(30));
        Assert.False(await SignedInAsync(apps.OnPrem, disabled));
    }

    // Renewed once a minute, an active sign-in still has its account checked every 5 minutes (CookieSecurityStamp): an
    // account disabled while it works loses the session at the next check, not when it stops working.
    [Fact]
    public async Task A_session_kept_busy_is_still_ended_within_minutes_of_its_account_being_disabled()
    {
        var account = await apps.CreateStaffAsync("Dr. Busy Disabled", StaffRoles.Clinician);
        var session = await SignInAsync(apps.OnPrem, account);
        for (var minute = 1; minute <= 3; minute++)
        {
            apps.Clock.Pass(TimeSpan.FromMinutes(1));
            Assert.True(await SignedInAsync(apps.OnPrem, session));
        }

        await using (var scope = apps.OnPrem.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
            var user = (await users.FindByIdAsync(account.Id.ToString()))!;
            Assert.True((await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddYears(100))).Succeeded);
        }

        // Four minutes after the sign-in's check, the next is still to come; at five it is made, and refuses.
        apps.Clock.Pass(TimeSpan.FromMinutes(1));
        Assert.True(await SignedInAsync(apps.OnPrem, session));
        apps.Clock.Pass(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(5));
        Assert.False(await SignedInAsync(apps.OnPrem, session));
    }

    [Fact]
    public async Task An_idle_session_ends_15_minutes_after_its_last_request_and_requests_keep_it()
    {
        var session = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Dr. Idle", StaffRoles.Clinician));
        for (var i = 0; i < 3; i++)
        {
            apps.Clock.Pass(TimeSpan.FromMinutes(14));
            Assert.True(await SignedInAsync(apps.OnPrem, session));
        }

        apps.Clock.Pass(JustOver);
        Assert.False(await SignedInAsync(apps.OnPrem, session));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/session");
        request.Headers.Add(SessionTimes.CheckHeader, "1");
        using var check = await Client(apps.OnPrem, session.Jar, followRedirects: false).SendAsync(request, Cancel);
        Assert.Equal(HttpStatusCode.Redirect, check.StatusCode);
    }

    [Fact]
    public async Task A_check_never_keeps_a_session()
    {
        var session = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Dr. Checked", StaffRoles.Clinician));
        apps.Clock.Pass(TimeSpan.FromMinutes(10));
        var (answer, _) = await AskAsync(session.Client, check: true);
        Assert.InRange((int)answer["left"]!, 290, 300);

        apps.Clock.Pass(TimeSpan.FromMinutes(6));
        Assert.False(await SignedInAsync(apps.OnPrem, session));
    }

    [Fact]
    public async Task A_patients_session_ends_the_same_way()
    {
        var patient = await apps.InsertPatientAsync("Synthetic", "Idle portal");
        var (client, jar) = await PortalResultsTests.SignInPatientAsync(apps, apps.OnPrem, patient);
        var (answer, _) = await AskAsync(client, check: true, "/portal/session");
        Assert.InRange((int)answer["left"]!, 890, 900);
        Assert.Null(answer["acting"]?.GetValue<string>());

        apps.Clock.Pass(TimeSpan.FromMinutes(14));
        Assert.Equal(HttpStatusCode.OK, (await Client(apps.OnPrem, jar, followRedirects: false).GetAsync("/portal", Cancel)).StatusCode);
        apps.Clock.Pass(JustOver);
        using var ended = await Client(apps.OnPrem, jar, followRedirects: false).GetAsync("/portal", Cancel);
        Assert.Equal(HttpStatusCode.Redirect, ended.StatusCode);
        Assert.Equal("/portal/sign-in", new Uri(Base, ended.Headers.Location!).AbsolutePath);
    }

    // Off-site, the cloud app answers the session page itself, never forwarding it, even with the clinic's server cut off.
    [Fact]
    public async Task The_cloud_app_answers_the_session_page_itself()
    {
        var account = await apps.CreateStaffAsync("Dr. Remote Session", StaffRoles.Clinician);
        var session = await SignInAsync(apps.Cloud, account);
        var (answer, _) = await AskAsync(session.Client, check: false);
        Assert.InRange((int)answer["left"]!, 890, 900);
        Assert.Equal(account.Id, Guid.Parse((string)answer["account"]!));
        Assert.DoesNotContain(apps.Forwarded, forwarded => forwarded.Contains("/session", StringComparison.Ordinal));

        var cutOff = Client(apps.CloudCutOff, session.Jar);
        var (offline, _) = await AskAsync(cutOff, check: true);
        Assert.InRange((int)offline["left"]!, 880, 900);
    }

    // Between the password and the code, the pending cookie still lasts 10 minutes and doesn't slide.
    [Fact]
    public async Task The_step_between_password_and_code_is_unchanged()
    {
        var account = await apps.CreateStaffAsync("Dr. Slow Code", StaffRoles.Clinician);
        var jar = new System.Net.CookieContainer();
        var client = Client(apps.OnPrem, jar);
        var codePage = await PostFormAsync(client, "/sign-in", "/sign-in", ("Input.Email", account.Email), ("Input.Password", account.Password));
        Assert.Equal("/sign-in/code", PathOf(codePage));
        apps.Clock.Pass(TimeSpan.FromMinutes(11));
        using var late = await PostFormAsync(client, "/sign-in/code", codePage, ("Code", Totp.Code(account.AuthenticatorKey)));
        Assert.StartsWith("/sign-in", PathOf(late), StringComparison.Ordinal);
        Assert.DoesNotContain("My patients", await ReadAsync(late));
    }

    // The longest a dictation and a visit record, as the dictation service's pinned start answers give them: what the
    // 12-hour end leaves time for before a recording starts (DictationApi.SignInEndsTooSoonAsync).
    [Fact]
    public async Task The_recording_lengths_the_sign_in_must_leave_time_for_are_the_services()
    {
        static async Task<int> MaxSecondsAsync(string name) =>
            (int)JsonNode.Parse(await File.ReadAllTextAsync(RepositoryRoot.Combine($"src/Ehr.Dictation/contract/{name}.json"), Cancel))!["body"]!["maxSeconds"]!;

        Assert.Equal(DictationApi.LongestDictation.TotalSeconds, await MaxSecondsAsync("start-201"));
        Assert.Equal(DictationApi.LongestVisit.TotalSeconds, await MaxSecondsAsync("start-ambient-201"));
    }
}
