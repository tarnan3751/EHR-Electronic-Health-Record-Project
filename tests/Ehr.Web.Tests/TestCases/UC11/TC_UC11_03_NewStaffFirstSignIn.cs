using System.Net;
using System.Text.RegularExpressions;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Testing;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC11;

// Test Case ID: TC-UC11-03
// Purpose / scenario: Only an administrator can create a staff account. The new account gets a one-time setup link,
//   chooses its password with it, and must enroll an authenticator app at its first sign-in before it reaches
//   anything.
// Type: Functional (alternative flow: first sign-in)
// Requirements: FR-25, FR-26
// Use case: UC 11, Sign in with multi-factor authentication
// Preconditions: A clinician account and an admin account exist, both set up. The new address has no account.
// Inputs and system conditions: POST /admin/staff as the clinician, then as the admin; GET and POST the setup link;
//   POST /sign-in; GET /patients; POST /sign-in/enroll with a wrong code, then the right one. On the on-prem app (the
//   on-site address). The clinic's server is up.
// Test data: Clinician "Dr. Not Admin" and admin "The Admin", with fresh @example.test addresses. New account: a fresh
//   @example.test address, Name to show "Nurse Test 01", role Clinician. Short password "short-pw-1" (10 characters);
//   new password "nurse-test-password-1234". The codes are computed from the key the enrollment page shows.
// Steps:
//   1. As the clinician, post the account creation form.
//   2. As the admin, post the same form for the new address.
//   3. In a new browser, open the setup link and enter the short password twice.
//   4. Enter the new password twice.
//   5. Open the setup link again.
//   6. Sign in with the new password, then open /patients before enrolling.
//   7. On the enrollment page, enter a wrong code, then the current code.
//   8. Open /patients, and read the audit log.
// Expected result: Step 1 ends on /denied and no account is created. Step 2 shows the setup link once ("It isn't shown
//   again"). Step 3 shows "Use at least 12 characters." Step 4 goes to /sign-in. Step 5 shows "This setup link isn't
//   valid or has already been used." Step 6 lands on /sign-in/enroll, and /patients redirects to /sign-in. Step 7 shows
//   "That code did not match.", then "Your recovery codes" with 8 codes. Step 8 opens the patients page (200), and the
//   log has one staff_invited event by the admin and one each of password_changed, mfa_enrolled and sign_in by the new
//   account.
// Creator: Luke Garcia
public partial class TC_UC11_03_NewStaffFirstSignIn(WebApps apps) : IClassFixture<WebApps>
{
    // StaffSignIn.RecoveryCodes: how many the enrollment page shows, once.
    const int RecoveryCodes = 8;

    [Fact]
    public async Task Only_an_admin_creates_staff_and_a_new_account_sets_its_password_and_authenticator_first()
    {
        // 1. A clinician can't create an account.
        var clinician = await apps.CreateStaffAsync("Dr. Not Admin", StaffRoles.Clinician);
        var clinicianSession = await SignInAsync(apps.OnPrem, clinician);
        var refusedEmail = WebApps.NewEmail();
        using var refused = await PostFormAsync(clinicianSession.Client, "/admin/staff", "/patients",
            ("Input.Email", refusedEmail), ("Input.DisplayName", "Not Created"), ("Input.Clinician", "true"), ("Input.Admin", "false"));
        Assert.Equal("/denied", PathOf(refused));
        Assert.Null(await StaffIdAsync(refusedEmail));

        // 2. An admin can, and gets a one-time setup link.
        var admin = await apps.CreateStaffAsync("The Admin", StaffRoles.Admin);
        var adminSession = await SignInAsync(apps.OnPrem, admin);
        var email = WebApps.NewEmail();
        using var invited = await PostFormAsync(adminSession.Client, "/admin/staff", "/admin/staff",
            ("Input.Email", email), ("Input.DisplayName", "Nurse Test 01"), ("Input.Clinician", "true"), ("Input.Admin", "false"));
        var invitedHtml = await ReadAsync(invited);
        Assert.Contains("It isn't shown again", invitedHtml);
        var setupPath = new Uri(WebUtility.HtmlDecode(SetupLink().Match(invitedHtml).Value)).PathAndQuery;
        var newId = await StaffIdAsync(email);
        Assert.NotNull(newId);

        // 3. The new person, in a browser of their own: a password under 12 characters is refused.
        var jar = new CookieContainer();
        var client = Client(apps.OnPrem, jar);
        using var tooShort = await PostFormAsync(client, setupPath, setupPath, ("Input.Password", "short-pw-1"), ("Input.Confirm", "short-pw-1"));
        Assert.Contains("Use at least 12 characters.", await ReadAsync(tooShort));

        // 4.
        const string password = "nurse-test-password-1234";
        using var afterSetup = await PostFormAsync(client, setupPath, setupPath, ("Input.Password", password), ("Input.Confirm", password));
        Assert.Equal("/sign-in", PathOf(afterSetup));

        // 5. The link works once.
        using var again = await client.GetAsync(setupPath, Cancel);
        Assert.Contains("This setup link isn't valid or has already been used.", await ReadAsync(again));

        // 6. The first sign-in goes to enrollment, and nothing is reachable before it's done.
        using var enrollPage = await PostFormAsync(client, "/sign-in", afterSetup, ("Input.Email", email), ("Input.Password", password));
        Assert.Equal("/sign-in/enroll", PathOf(enrollPage));
        using var early = await Client(apps.OnPrem, jar, followRedirects: false).GetAsync("/patients", Cancel);
        Assert.Equal("/sign-in", new Uri(Base, early.Headers.Location!).AbsolutePath);

        // 7.
        var key = AuthenticatorSecret().Match(await enrollPage.Content.ReadAsStringAsync(Cancel)).Groups[1].Value;
        using var wrong = await PostFormAsync(client, "/sign-in/enroll", enrollPage, ("Code", Totp.WrongCode(key)));
        Assert.Contains("That code did not match.", await ReadAsync(wrong));
        using var recoveryPage = await PostFormAsync(client, "/sign-in/enroll", wrong, ("Code", Totp.Code(key)));
        var recoveryHtml = await ReadAsync(recoveryPage);
        Assert.Contains("Your recovery codes", recoveryHtml);
        Assert.Equal(RecoveryCodes, RecoveryCode().Matches(recoveryHtml).Count);

        // 8.
        using var patients = await Client(apps.OnPrem, jar, followRedirects: false).GetAsync("/patients", Cancel);
        Assert.Equal(HttpStatusCode.OK, patients.StatusCode);
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.StaffInvited, actorId: admin.Id));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.PasswordChanged, actorId: newId));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.MfaEnrolled, actorId: newId));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.SignIn, actorId: newId));
    }

    async Task<Guid?> StaffIdAsync(string email)
    {
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT id FROM ehr.staff_users WHERE email = @email", db);
        command.Parameters.AddWithValue("email", email);
        return (Guid?)await command.ExecuteScalarAsync(Cancel);
    }

    [GeneratedRegex("secret=([A-Z2-7]+)")]
    private static partial Regex AuthenticatorSecret();

    [GeneratedRegex("<li class=\"mono-code\">")]
    private static partial Regex RecoveryCode();

    [GeneratedRegex("https://[^<\\s]+/sign-in/setup\\?user=[^<\\s]+")]
    private static partial Regex SetupLink();
}
