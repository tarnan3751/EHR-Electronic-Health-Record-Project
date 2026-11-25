using Ehr.Data.Accounts;
using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.ProxyTests;

namespace Ehr.Web.Tests.TestCases.UC18;

// Test Case ID: TC-UC18-06
// Purpose / scenario: Recording and removing a proxy at the clinic is keyboard-first: from the chart to the saved
//   proxy, and its removal, with no mouse, in a real browser.
// Type: Non-functional (usability)
// Requirements: NFR-12
// Use case: UC 18, Give someone proxy access to a record
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel. The proxy has a portal
//   account. Chromium, driven by Playwright, with the keyboard only.
// Inputs and system conditions: Key presses only: Tab, Enter and typing.
// Test data: The proxy's email, typed. The scope and length of time as the form offers them first.
// Steps:
//   1. Open the chart. Press Tab until "Who can use the portal for this patient" has the focus, and press Enter.
//   2. Type the proxy's email (the cursor is already in its field), press Tab until "Record proxy" has the focus, and
//      press Enter.
//   3. Press Tab until "Remove" has the focus, and press Enter.
// Expected result: Step 1 opens the proxies page with the cursor in the email field. Step 2 saves: the page says
//   "Saved:" and lists the proxy as in force. Step 3 removes it: the page says "Removed:". Every focused control shows
//   the focus ring, and the page reports no script errors.
// Creator: Tahseen Arnan
//
// In the browser tests' collection, which doesn't run beside the others (AiReviewBrowserTests says why).
[Collection(BrowserTests.Collection)]
public class TC_UC18_06_Nfr12_KeyboardFirst(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Recording_and_removing_a_proxy_works_from_the_keyboard()
    {
        var patient = await PersonAsync(apps, "Synthetic", "Keyed");
        var proxy = await PersonAsync(apps, "Synthetic", "Typist");
        var clinician = await apps.CreateStaffAsync("Dr. Proxy Keys", StaffRoles.Clinician);
        await apps.AssignAsync(clinician, patient.Patient);
        var staff = await SignInAsync(apps.OnPrem, clinician);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await AiReviewBrowserTests.ContextAsync(browser, staff);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        const string focusRing = "getComputedStyle(document.activeElement).outlineStyle !== 'none'";

        await page.GotoAsync($"{server.Url}/patients/{patient.Patient}");
        await AiReviewBrowserTests.TabToAsync(page, "a[data-proxies]");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/proxies");
        await page.WaitForFunctionAsync("document.activeElement?.matches('#proxy-email') === true");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));

        await page.Keyboard.TypeAsync(proxy.Email);
        await AiReviewBrowserTests.TabToAsync(page, "form.sc-form button[type=submit]");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync(url => url.Contains("done=recorded", StringComparison.Ordinal));
        Assert.Contains("Saved:", await page.Locator(".sc-notice--success").TextContentAsync());
        Assert.Equal(1, await page.Locator("tr[data-grant='in-force']").CountAsync());

        await AiReviewBrowserTests.TabToAsync(page, "tr[data-grant='in-force'] button");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync(url => url.Contains("done=removed", StringComparison.Ordinal));
        Assert.Contains("Removed:", await page.Locator(".sc-notice--success").TextContentAsync());

        Assert.Equal([$"{proxy.Email} record false staff revoked"], await GrantsAsync(apps, patient.Patient));
        Assert.Empty(errors);
    }
}
