using System.Net;
using System.Text.RegularExpressions;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// What an admin does to an existing staff account at /admin/staff (StaffAccounts): a new setup link for a forgotten
// password, an authenticator reset for a lost phone, and disabling and enabling the account. Never their own.
public partial class StaffAdminTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_setup_link_replaces_a_forgotten_password()
    {
        var admin = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Resetting Admin", StaffRoles.Admin));
        var account = await apps.CreateStaffAsync("Dr. Forgot", StaffRoles.Clinician);

        using var reissued = await PostFormAsync(admin.Client, "/admin/staff?handler=Reissue", "/admin/staff", ("id", account.Id));
        var setupLink = WebUtility.HtmlDecode(SetupLink().Match(await ReadAsync(reissued)).Value);

        // The old password is over; the link sets the next one, and the authenticator stays enrolled.
        var client = Client(apps.OnPrem, new CookieContainer());
        using var oldPassword = await PostFormAsync(client, "/sign-in", "/sign-in", ("Input.Email", account.Email), ("Input.Password", account.Password));
        Assert.Contains("Wrong email or password.", await ReadAsync(oldPassword));

        var setupPath = new Uri(setupLink).PathAndQuery;
        const string newPassword = "chosen-after-the-reset";
        using var afterSetup = await PostFormAsync(client, setupPath, setupPath, ("Input.Password", newPassword), ("Input.Confirm", newPassword));
        Assert.Contains("Your password is set.", await ReadAsync(afterSetup));
        var session = await SignInAsync(apps.OnPrem, account with { Password = newPassword });
        Assert.Contains("Dr. Forgot", await session.Client.GetStringAsync("/", Cancel));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.PasswordReset));
    }

    [Fact]
    public async Task A_reset_authenticator_is_enrolled_again_at_the_next_sign_in()
    {
        var admin = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Phone Admin", StaffRoles.Admin));
        var account = await apps.CreateStaffAsync("Dr. Lost Phone", StaffRoles.Clinician);

        using var reset = await PostFormAsync(admin.Client, "/admin/staff?handler=ResetAuthenticator", "/admin/staff", ("id", account.Id));
        Assert.Contains("will set up an authenticator app again", await ReadAsync(reset));

        var client = Client(apps.OnPrem, new CookieContainer());
        using var next = await PostFormAsync(client, "/sign-in", "/sign-in", ("Input.Email", account.Email), ("Input.Password", account.Password));
        Assert.Equal("/sign-in/enroll", PathOf(next));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.AuthenticatorReset));
    }

    [Fact]
    public async Task A_disabled_account_cannot_sign_in_until_it_is_enabled_again()
    {
        var admin = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Leaving Admin", StaffRoles.Admin));
        var account = await apps.CreateStaffAsync("Dr. Leaving", StaffRoles.Clinician);
        var token = await apps.DesktopSignInAsync(apps.OnPrem, account);

        using var disabled = await PostFormAsync(admin.Client, "/admin/staff?handler=Disable", "/admin/staff", ("id", account.Id));
        var staffPage = await ReadAsync(disabled);
        Assert.Contains("is disabled and can no longer sign in", staffPage);
        Assert.Contains("<td>Disabled</td>", staffPage);

        var client = Client(apps.OnPrem, new CookieContainer());
        using var refused = await PostFormAsync(client, "/sign-in", "/sign-in", ("Input.Email", account.Email), ("Input.Password", account.Password));
        Assert.Contains("This account is locked", await ReadAsync(refused));
        using var desktop = CreateClient(apps.OnPrem);
        desktop.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var sync = await desktop.GetAsync("/api/sync/quick-texts?since=0", Cancel);
        Assert.Equal(HttpStatusCode.Unauthorized, sync.StatusCode);

        using var enabled = await PostFormAsync(admin.Client, "/admin/staff?handler=Enable", "/admin/staff", ("id", account.Id));
        Assert.Contains("can sign in again", await ReadAsync(enabled));
        var session = await SignInAsync(apps.OnPrem, account);
        Assert.Contains("Dr. Leaving", await session.Client.GetStringAsync("/", Cancel));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.AccountDisabled));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.AccountEnabled));
    }

    [Fact]
    public async Task An_admin_cannot_act_on_their_own_account()
    {
        var self = await apps.CreateStaffAsync("Self Admin", StaffRoles.Admin);
        var admin = await SignInAsync(apps.OnPrem, self);

        using var response = await PostFormAsync(admin.Client, "/admin/staff?handler=Disable", "/admin/staff", ("id", self.Id));

        Assert.Contains("not one you can change here", await ReadAsync(response));
        using var still = await Client(apps.OnPrem, admin.Jar, followRedirects: false).GetAsync("/admin/staff", Cancel);
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);
    }

    [GeneratedRegex("https://[^<\\s]+/sign-in/setup\\?user=[^<\\s]+")]
    private static partial Regex SetupLink();
}
