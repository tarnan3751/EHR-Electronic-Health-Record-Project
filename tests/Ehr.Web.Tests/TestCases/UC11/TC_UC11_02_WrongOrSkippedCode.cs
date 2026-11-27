using System.Net;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Testing;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC11;

// Test Case ID: TC-UC11-02
// Purpose / scenario: Off-site, a correct password is never enough. A wrong authenticator code is refused and recorded,
//   and going around the code page to a patient page sends the browser back to sign in.
// Type: Functional (invalid input)
// Requirements: FR-28
// Use case: UC 11, Sign in with multi-factor authentication
// Preconditions: A clinician account exists, is active, and has finished setup with an authenticator app. The
//   browser is signed out.
// Inputs and system conditions: POST the email and correct password to /sign-in, then a wrong code to /sign-in/code,
//   then GET /patients without a code, all on the cloud app (the off-site address). The clinic's server is up.
// Test data: A new clinician, "Dr. Wrong Code", with a fresh @example.test address and the fixture's staff password.
//   The wrong code is the account's real code for a time step far from now (Totp.WrongCode), so it is six digits the
//   app would not show.
// Steps:
//   1. Enter the email and the correct password.
//   2. On the code page, enter the wrong code.
//   3. Without a correct code, open /patients directly.
//   4. Read the audit log for the account.
// Expected result: Step 2 stays on /sign-in/code with "Wrong code." Step 3 is redirected to /sign-in and shows no patient data. Step 4 finds no sign_in event and a
//   sign_in_failed event with the reason "wrong code".
// Creator: Luke Garcia
public class TC_UC11_02_WrongOrSkippedCode(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Off_site_a_wrong_or_skipped_code_signs_nobody_in()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Wrong Code", StaffRoles.Clinician);
        var jar = new CookieContainer();
        var client = Client(apps.Cloud, jar);

        using var codePage = await PostFormAsync(client, "/sign-in", "/sign-in", ("Input.Email", clinician.Email), ("Input.Password", clinician.Password));
        using var wrong = await PostFormAsync(client, "/sign-in/code", codePage, ("Code", Totp.WrongCode(clinician.AuthenticatorKey)));
        Assert.Equal("/sign-in/code", PathOf(wrong));
        Assert.Contains("Wrong code.", await ReadAsync(wrong));

        using var skipped = await Client(apps.Cloud, jar, followRedirects: false).GetAsync("/patients", Cancel);
        Assert.Equal(HttpStatusCode.Redirect, skipped.StatusCode);
        Assert.Equal("/sign-in", new Uri(Base, skipped.Headers.Location!).AbsolutePath);

        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.SignIn, actorId: clinician.Id));
        Assert.Contains("wrong code", await SignInFailureReasonsAsync(clinician.Id));
    }

    async Task<List<string>> SignInFailureReasonsAsync(Guid actorId)
    {
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "SELECT reason FROM ehr.audit_events WHERE action = @action AND actor_id = @actor AND reason IS NOT NULL", db);
        command.Parameters.AddWithValue("action", AuditActions.SignInFailed);
        command.Parameters.AddWithValue("actor", actorId);
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        var reasons = new List<string>();
        while (await reader.ReadAsync(Cancel))
        {
            reasons.Add(reader.GetString(0));
        }

        return reasons;
    }
}
