using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Domain.Accounts;
using Ehr.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.PasskeyFlows;

namespace Ehr.Web.Tests.TestCases.UC11;

// Test Case ID: TC-UC11-17
// Purpose / scenario: One passkey works on both staff sites (the expansion plan's step 19; Tahseen's decision 8a): added
//   at the clinic, it signs the clinician in off-site too, where the cloud app forwards both of its posts to the
//   clinic's server, which checks the passkey and keeps its count; the replica is never written. The authenticator code
//   still signs in an account that has a passkey.
// Type: Functional (alternative flow)
// Requirements: proposed FR-67, FR-28
// Use case: UC 11, Sign in with multi-factor authentication
// Preconditions: Dr. Two Sites is active with an authenticator app and signed in on-site. Passkeys are tied to the
//   tests' parent domain, example.test, from the on-site and off-site addresses under it.
// Inputs and system conditions: POST /passkeys (the name and a code, then the passkey) on-site; then, signed out, the
//   password and the passkey on the cloud app; then on-site; then the password and the code.
// Test data: A software authenticator (TestAuthenticator) that verifies the person; the passkey named "This Mac".
// Steps:
//   1. Add the passkey on-site.
//   2. Sign in off-site with the password, then the passkey.
//   3. Sign in on-site with the password, then the passkey.
//   4. Sign in with the password, then the authenticator code.
// Expected result: Step 1: "Passkey added". Step 2: signed in, on the clinician's patients; the cloud app forwarded
//   POST /sign-in/code?handler=Options and ?handler=Passkey; the passkey's count is 1. Step 3: signed in; its count is
//   2. Step 4: signed in. The audit log has two sign_in events naming the passkey, and one with no details.
// Creator: Tahseen Arnan
public class TC_UC11_17_OnePasskeyBothSites(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_passkey_added_on_site_signs_in_off_site_and_on_site_and_the_code_still_works()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Two Sites", StaffRoles.Clinician);
        using var authenticator = new TestAuthenticator();

        // 1.
        using (var added = await AddAsync((await SignInAsync(apps.OnPrem, clinician)).Client, clinician, authenticator))
        {
            Assert.Contains("Passkey added.", Words(await ReadAsync(added)));
        }

        // 2.
        using (var offSite = await SignInWithPasskeyAsync(apps.Cloud, clinician, authenticator, WebApps.OffSiteOrigin))
        {
            Assert.Equal("/patients", PathOf(offSite));
            Assert.Contains("Dr. Two Sites", await ReadAsync(offSite));
        }

        Assert.Contains($"POST {CodePage}?handler=Options", apps.Forwarded);
        Assert.Contains($"POST {CodePage}?handler=Passkey", apps.Forwarded);
        Assert.Equal(1u, (await StoredAsync(clinician, authenticator)).SignCount);

        // 3.
        using (var onSite = await SignInWithPasskeyAsync(apps.OnPrem, clinician, authenticator))
        {
            Assert.Equal("/patients", PathOf(onSite));
        }

        Assert.Equal(2u, (await StoredAsync(clinician, authenticator)).SignCount);

        // 4.
        await SignInAsync(apps.OnPrem, clinician);

        var reference = StaffPasskeys.Reference(authenticator.CredentialId);
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT coalesce(details ->> 'passkey', '') FROM ehr.audit_events WHERE action = @action AND actor_id = @actor ORDER BY occurred_at, id", connection);
        command.Parameters.AddWithValue("action", AuditActions.SignIn);
        command.Parameters.AddWithValue("actor", clinician.Id);
        var signIns = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                signIns.Add(reader.GetString(0));
            }
        }

        // The sign-in that added the passkey, the two with it, and the one with the code.
        Assert.Equal(["", reference, reference, ""], signIns);
    }

    async Task<UserPasskeyInfo> StoredAsync(StaffAccount account, TestAuthenticator authenticator)
    {
        await using var scope = apps.OnPrem.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        return (await users.GetPasskeyAsync((await users.FindByIdAsync(account.Id.ToString()))!, authenticator.CredentialId))!;
    }
}
