using System.Net;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Testing;
using Ehr.Web.CloudAudit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.PasskeyFlows;

namespace Ehr.Web.Tests.TestCases.UC11;

// Test Case ID: TC-UC11-19
// Purpose / scenario: A passkey that can't be used never costs the clinician the sign-in (the expansion plan's step 19):
//   when the device can't verify them, or the prompt is cancelled, the page says so where the focus is, and the code,
//   which is always on the page, signs them in without the password again. Off-site with the clinic's server cut off,
//   a passkey can't be used, as no sign-in can (UC 2), and the page's script is told so rather than given options.
// Type: Functional (exceptional condition)
// Requirements: proposed FR-67, FR-28, FR-34
// Use case: UC 11, Sign in with multi-factor authentication
// Preconditions: Dr. Cancelled Passkey is active with an authenticator app and a passkey on Chromium's virtual
//   authenticator, added on the passkeys page; the authenticator then can't verify the person. The app reached at the
//   test's own address.
// Inputs and system conditions: /sign-in, /sign-in/code; the keyboard; then the options' post on the cloud app with its
//   link to the clinic's server down.
// Test data: The passkey named "Work laptop".
// Steps:
//   1. Give the password; press Enter on "Use a passkey".
//   2. Tab to the code, type it, press Enter.
//   3. Off-site, cut off, after the password, post for a passkey's options.
// Expected result: Step 1: still on /sign-in/code, a notice in an alert: "Not signed in: the passkey wasn't used. It may
//   have been cancelled, or this device couldn't check your PIN, fingerprint or face. Try again, or enter a code
//   below."; the focus on "Use a passkey"; nothing reached the server, so no failed sign-in is recorded. Step 2: signed
//   in, to the clinician's patients. Step 3: 503, not JSON, which the script reads as the server unreachable.
// Creator: Tahseen Arnan
[Collection(BrowserTests.Collection)]
public class TC_UC11_19_PasskeyNotUsed(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_passkey_not_used_says_so_and_the_code_signs_in_without_the_password_again()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Cancelled Passkey", StaffRoles.Clinician);
        WebApplicationFactory<Program>? app = null;
        await using var server = await ClinicalBrowserHost.StartAsync(url => app = AppAt(apps, url));
        await using var made = app!;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = 1280, Height = 800 } });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        var (devTools, authenticator) = await VirtualAuthenticatorAsync(context, page);
        await PasswordInBrowserAsync(page, server.Url, clinician);
        await page.Keyboard.TypeAsync(Totp.Code(clinician.AuthenticatorKey));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/patients");
        await AddInBrowserAsync(page, server.Url, clinician, "Work laptop");
        await page.ClickAsync(".sc-appbar__signout");
        await page.WaitForURLAsync("**/sign-out");
        await devTools.SendAsync("WebAuthn.setUserVerified", new Dictionary<string, object> { ["authenticatorId"] = authenticator, ["isUserVerified"] = false });
        var failedBefore = await apps.AuditEventsAsync(AuditActions.SignInFailed, actorId: clinician.Id);

        // 1.
        await PasswordInBrowserAsync(page, server.Url, clinician);
        await page.WaitForSelectorAsync("[data-passkey-use]:not([hidden])");
        await page.Keyboard.PressAsync("Enter");
        var notice = page.Locator("[data-passkey-use] [role=alert]");
        await notice.WaitForAsync();
        Assert.Equal(
            "Not signed in: the passkey wasn't used. It may have been cancelled, or this device couldn't check your PIN, fingerprint or face. Try again, or enter a code below.",
            (await notice.InnerTextAsync()).Trim());
        Assert.EndsWith(CodePage, new Uri(page.Url).AbsolutePath);
        Assert.Equal("Use a passkey", await page.EvaluateAsync<string>("document.activeElement.textContent.trim()"));
        Assert.Equal(failedBefore, await apps.AuditEventsAsync(AuditActions.SignInFailed, actorId: clinician.Id));

        // 2.
        await AiReviewBrowserTests.TabToAsync(page, "#Code");
        await page.Keyboard.TypeAsync(Totp.Code(clinician.AuthenticatorKey));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/patients");
        Assert.Empty(errors);

        // 3.
        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(TestContext.Current.CancellationToken));
        var jar = new CookieContainer();
        var (_, codePage) = await PasswordAsync(apps.OnPrem, clinician, jar);
        using var options = await OptionsToSignInAsync(Client(apps.CloudCutOff, jar), codePage);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, options.StatusCode);
        Assert.DoesNotContain("application/json", options.Content.Headers.ContentType?.MediaType ?? "", StringComparison.Ordinal);
    }
}
