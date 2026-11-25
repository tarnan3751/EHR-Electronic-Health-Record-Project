using System.Security.Cryptography;
using Ehr.Data.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ehr.Web.Tests;

// The passkeys Identity's store keeps for a staff account (step 19, ehr.staff_user_passkeys), through the same
// UserManager the app uses, as the on-prem app's role: one added, found by its credential's ID, its count kept, listed
// and removed. And a patient account has none: the patient store's passkey type is mapped to no table.
public class PasskeyStoreTests(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_staff_passkey_is_added_found_counted_and_removed()
    {
        var account = await apps.CreateStaffAsync("Dr. Passkey Store", StaffRoles.Clinician);
        await using var scope = apps.OnPrem.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        var user = (await users.FindByIdAsync(account.Id.ToString()))!;
        var credentialId = RandomNumberGenerator.GetBytes(32);
        var passkey = new UserPasskeyInfo(
            credentialId, RandomNumberGenerator.GetBytes(77), DateTimeOffset.UtcNow, signCount: 0, ["internal", "hybrid"],
            isUserVerified: true, isBackupEligible: true, isBackedUp: true, RandomNumberGenerator.GetBytes(120), RandomNumberGenerator.GetBytes(90))
        { Name = "This Mac" };

        Assert.True((await users.AddOrUpdatePasskeyAsync(user, passkey)).Succeeded);

        Assert.Equal(user.Id, (await users.FindByPasskeyIdAsync(credentialId))!.Id);
        var stored = Assert.Single(await users.GetPasskeysAsync(user));
        Assert.Equal("This Mac", stored.Name);
        Assert.Equal(passkey.PublicKey, stored.PublicKey);
        Assert.Equal(["internal", "hybrid"], stored.Transports!);
        Assert.True(stored.IsUserVerified && stored.IsBackupEligible && stored.IsBackedUp);

        stored.SignCount = 7;
        Assert.True((await users.AddOrUpdatePasskeyAsync(user, stored)).Succeeded);
        Assert.Equal(7u, (await users.GetPasskeyAsync(user, credentialId))!.SignCount);

        Assert.True((await users.RemovePasskeyAsync(user, credentialId)).Succeeded);
        Assert.Empty(await users.GetPasskeysAsync(user));
        Assert.Null(await users.FindByPasskeyIdAsync(credentialId));
    }

    [Fact]
    public async Task A_patient_account_has_no_passkeys()
    {
        var (email, _) = await apps.CreatePatientAccountAsync(await apps.InsertPatientAsync("Pat", "Passkeyless"));
        await using var scope = apps.OnPrem.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PatientUser>>();
        var user = (await users.FindByEmailAsync(email))!;

        await Assert.ThrowsAsync<InvalidOperationException>(() => users.GetPasskeysAsync(user));
    }
}
