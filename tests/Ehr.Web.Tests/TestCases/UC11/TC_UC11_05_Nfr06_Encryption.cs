using System.Net;
using Ehr.Data.Accounts;
using Ehr.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC11;

// Test Case ID: TC-UC11-05
// Purpose / scenario: What a sign-in sends and stores is protected. The session cookie is only sent over HTTPS, can't
//   be read by scripts, and isn't sent with other sites' requests; the password is stored as a hash; the keys that
//   protect cookies, the authenticator key and the recovery codes are encrypted before they are stored.
// Type: Non-functional (security: encryption in transit and at rest)
// Requirements: NFR-06, with FR-28
// Use case: UC 11, Sign in with multi-factor authentication
// Preconditions: A clinician account exists and has finished setup with an authenticator app. The apps have the Data
//   Protection certificate, as the stack's do.
// Inputs and system conditions: POST the email and password, then the code, on the cloud app (the off-site address),
//   reading the Set-Cookie header of the answer to the code; then, as the database superuser, SELECT the account's
//   password_hash from ehr.staff_users, every xml from ehr.data_protection_keys, and the account's AuthenticatorKey and
//   RecoveryCodes values from ehr.staff_user_tokens.
// Test data: Clinicians "Dr. Cookie", "Dr. Hashed", "Dr. Secret" and "Dr. Recovery", with fresh @example.test
//   addresses and the fixture's staff password, "a-long-enough-password".
// Steps:
//   1. Sign in off-site with the password and code, and read the __Host-Ehr.Staff cookie the code's answer sets.
//   2. Sign in on-site, then read the account's stored password.
//   3. Read the stored Data Protection keys.
//   4. Read the stored authenticator key, and check the account's codes still work.
//   5. Make eight recovery codes, read what is stored, and redeem one.
// Expected result: Step 1's cookie has the Secure, HttpOnly and SameSite=Strict flags. Step 2's column holds an ASP.NET
//   Identity hash (starting "AQAAAA"), never the password. Step 3 finds at least one key, and every key holds an
//   encryptedSecret. Step 4's value starts "dp1:" and isn't the key, and a code made from the key signs in. Step 5's
//   value starts "dp1:" and holds none of the codes, and the code redeems, leaving seven. The parts that need the
//   running stack (database connections, plain HTTP and the disk) are the manual part, TC_UC11_05_Nfr06_Encryption.md
//   in this folder.
// Creator: Luke Garcia
public class TC_UC11_05_Nfr06_Encryption(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task The_staff_session_cookie_is_secure_http_only_and_same_site_strict()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Cookie", StaffRoles.Clinician);
        var jar = new CookieContainer();
        using var codePage = await PostFormAsync(Client(apps.Cloud, jar), "/sign-in", "/sign-in", ("Input.Email", clinician.Email), ("Input.Password", clinician.Password));

        using var signedIn = await PostFormAsync(Client(apps.Cloud, jar, followRedirects: false), "/sign-in/code", codePage, ("Code", Totp.Code(clinician.AuthenticatorKey)));

        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        var cookie = Assert.Single(signedIn.Headers.GetValues("Set-Cookie"), header => header.StartsWith("__Host-Ehr.Staff=", StringComparison.Ordinal));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Passwords_are_stored_hashed_and_cookie_keys_encrypted()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Hashed", StaffRoles.Clinician);
        await SignInAsync(apps.OnPrem, clinician);

        await using var db = await apps.OpenDatabaseAsync();
        await using (var command = new NpgsqlCommand("SELECT password_hash FROM ehr.staff_users WHERE id = @id", db))
        {
            command.Parameters.AddWithValue("id", clinician.Id);
            var hash = (string)(await command.ExecuteScalarAsync(Cancel))!;
            Assert.DoesNotContain(clinician.Password, hash);
            // ASP.NET Identity's v3 format: PBKDF2, with the version, settings and salt in front.
            Assert.StartsWith("AQAAAA", hash);
        }

        await using (var command = new NpgsqlCommand("SELECT xml FROM ehr.data_protection_keys", db))
        await using (var reader = await command.ExecuteReaderAsync(Cancel))
        {
            var keys = 0;
            while (await reader.ReadAsync(Cancel))
            {
                keys++;
                Assert.Contains("encryptedSecret", reader.GetString(0));
            }

            Assert.True(keys > 0, "No Data Protection key was stored.");
        }
    }

    // ProtectedTokenStore encrypts the authenticator key before it is saved, so a copy of the table, the replica or a
    // backup can't make codes. The app still reads it: the sign-in below uses a code made from the key it returns.
    [Fact]
    public async Task The_authenticator_key_is_not_stored_readable()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Secret", StaffRoles.Clinician);

        var stored = await StoredTokenAsync(clinician.Id, "AuthenticatorKey");

        Assert.StartsWith("dp1:", stored);
        Assert.DoesNotContain(clinician.AuthenticatorKey, stored);
        await SignInAsync(apps.OnPrem, clinician);
    }

    // The unused recovery codes are kept in the same table, and each one signs in like a code; they are encrypted too.
    [Fact]
    public async Task Recovery_codes_are_not_stored_readable()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Recovery", StaffRoles.Clinician);
        await using var scope = apps.OnPrem.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        var user = (await users.FindByIdAsync(clinician.Id.ToString()))!;
        var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 8))!.ToList();

        var stored = await StoredTokenAsync(clinician.Id, "RecoveryCodes");

        Assert.StartsWith("dp1:", stored);
        Assert.All(codes, code => Assert.DoesNotContain(code, stored));
        Assert.True((await users.RedeemTwoFactorRecoveryCodeAsync(user, codes[0])).Succeeded);
        Assert.Equal(7, await users.CountRecoveryCodesAsync(user));
    }

    // What ehr.staff_user_tokens holds for the account, read as the database superuser, past the app.
    async Task<string> StoredTokenAsync(Guid userId, string name)
    {
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "SELECT value FROM ehr.staff_user_tokens WHERE user_id = @id AND name = @name", db);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("name", name);
        var stored = (string?)await command.ExecuteScalarAsync(Cancel);
        Assert.NotNull(stored);
        return stored;
    }
}
