using System.Security.Cryptography;
using Ehr.Data.Accounts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ehr.Web.Tests;

// The passkeys Identity's store keeps for a staff account (step 19, ehr.staff_user_passkeys), through the same
// UserManager the app uses, as the on-prem app's role: one added, found by its credential's ID, its count kept, listed
// and removed. And a patient account has none: the patient store's passkey type is mapped to no table, and only staff
// have a passkey handler, which holds to Ehr:Passkeys.
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

    // The handler is staff's alone, and checks what the settings say: the domain, the person verified by the device, and
    // only the staff sites' own addresses, never a cross-origin frame.
    [Fact]
    public async Task Only_staff_have_a_passkey_handler_and_it_holds_to_the_settings()
    {
        await using var scope = apps.OnPrem.Services.CreateAsyncScope();
        Assert.NotNull(scope.ServiceProvider.GetService<IPasskeyHandler<StaffUser>>());
        Assert.Null(scope.ServiceProvider.GetService<IPasskeyHandler<PatientUser>>());

        var options = scope.ServiceProvider.GetRequiredService<IOptions<IdentityPasskeyOptions>>().Value;
        Assert.Equal(WebApps.PasskeyDomain, options.ServerDomain);
        Assert.Equal("required", options.UserVerificationRequirement);
        foreach (var (origin, crossOrigin, allowed) in new[]
        {
            (WebApps.OnSiteOrigin, false, true), (WebApps.OffSiteOrigin, false, true), (WebApps.OnSiteOrigin, true, false),
            ("https://portal.example.test", false, false), ("https://other.example.test", false, false), ("http://ehr.example.test", false, false),
        })
        {
            var context = new PasskeyOriginValidationContext { HttpContext = new DefaultHttpContext(), Origin = origin, CrossOrigin = crossOrigin };
            Assert.Equal(allowed, await options.ValidateOrigin!(context));
        }
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
