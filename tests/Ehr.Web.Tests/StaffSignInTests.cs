using System.Net;
using System.Text.RegularExpressions;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;

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
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.MfaEnrolled));
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

    [GeneratedRegex("<li class=\"code\">")]
    private static partial Regex RecoveryCode();

    [GeneratedRegex("https://[^<\\s]+/sign-in/setup\\?user=[^<\\s]+")]
    private static partial Regex SetupLink();
}
