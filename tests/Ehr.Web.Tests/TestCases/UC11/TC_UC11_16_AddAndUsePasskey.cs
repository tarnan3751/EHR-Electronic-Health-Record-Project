using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.PasskeyFlows;

namespace Ehr.Web.Tests.TestCases.UC11;

// Test Case ID: TC-UC11-16
// Purpose / scenario: A clinician adds a passkey and signs in with it in place of the authenticator code (the expansion
//   plan's step 19; Tahseen's decisions 6a and 7a): after the password, "Use a passkey" comes first, with the focus, and
//   the device verifies the person. Removed, the passkey is no longer offered. All from the keyboard.
// Type: Functional (successful flow)
// Requirements: proposed FR-67, FR-28, NFR-12
// Use case: UC 11, Sign in with multi-factor authentication
// Preconditions: Dr. Touch Sign-In is active with an authenticator app and has no passkey. Chromium, driven by
//   Playwright, with its virtual authenticator, a device that verifies the person; the app reached at the test's own
//   address, which passkeys are tied to.
// Inputs and system conditions: /sign-in, /sign-in/code, /passkeys; the keyboard.
// Test data: The passkey named "This Mac".
// Steps:
//   1. Sign in with the password and the code.
//   2. On the passkeys page, type the name and a current code, and press Enter.
//   3. Sign out, give the password, and press Enter on the focused "Use a passkey".
//   4. Remove the passkey; sign out; give the password.
// Expected result: Step 1: the code page offers no passkey. Step 2: "Passkey added: use it at your next sign-in, after
//   your password." with "This Mac" listed. Step 3: the code page shows "Use a passkey" with the focus on it, and the
//   code below; Enter signs in, to the clinician's patients, and the sign-in names the passkey in the audit log. Step
//   4: "Passkey removed"; the code page offers no passkey. No script error on any page.
// Creator: Tahseen Arnan
[Collection(BrowserTests.Collection)]
public class TC_UC11_16_AddAndUsePasskey(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_passkey_added_on_its_page_signs_in_in_place_of_the_code_and_once_removed_is_not_offered()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Touch Sign-In", StaffRoles.Clinician);
        WebApplicationFactory<Program>? app = null;
        await using var server = await ClinicalBrowserHost.StartAsync(url => app = AppAt(apps, url));
        await using var made = app!;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = 1280, Height = 800 } });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await VirtualAuthenticatorAsync(context, page);

        // 1.
        await PasswordInBrowserAsync(page, server.Url, clinician);
        Assert.Equal(0, await page.Locator("[data-passkey-use]").CountAsync());
        await CodeInBrowserAsync(page, clinician);
        await page.WaitForURLAsync("**/patients");

        // 2.
        await AddInBrowserAsync(page, server.Url, clinician, "This Mac");
        Assert.Contains("This Mac", await page.ContentAsync());

        // 3.
        await page.ClickAsync(".sc-appbar__signout");
        await page.WaitForURLAsync("**/sign-out");
        await PasswordInBrowserAsync(page, server.Url, clinician);
        await page.WaitForSelectorAsync("[data-passkey-use]:not([hidden])");
        Assert.Equal("Use a passkey", await page.EvaluateAsync<string>("document.activeElement.textContent.trim()"));
        Assert.True(await page.IsVisibleAsync("#Code"));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/patients");
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.PasskeyAdded, actorId: clinician.Id));
        await using (var connection = await apps.OpenDatabaseAsync())
        {
            await using var command = new Npgsql.NpgsqlCommand(
                "SELECT count(*) FROM ehr.audit_events WHERE action = 'sign_in' AND actor_id = @actor AND details ->> 'passkey' IS NOT NULL", connection);
            command.Parameters.AddWithValue("actor", clinician.Id);
            Assert.Equal(1L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        // 4.
        await page.GotoAsync($"{server.Url}{PasskeysPage}");
        await page.FocusAsync("form[action*='handler=Remove'] button");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForSelectorAsync("text=Passkey removed: it can't be used to sign in any more.");
        await page.ClickAsync(".sc-appbar__signout");
        await page.WaitForURLAsync("**/sign-out");
        await PasswordInBrowserAsync(page, server.Url, clinician);
        Assert.Equal(0, await page.Locator("[data-passkey-use]").CountAsync());
        Assert.Empty(errors);
    }
}
