using Ehr.Data.Accounts;
using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.SessionBrowser;

namespace Ehr.Web.Tests.TestCases.UC11;

// Test Case ID: TC-UC11-15
// Purpose / scenario: "Stay signed in" when the server can't be reached (the expansion plan's step 18): the warning
//   stays open and says so, rather than closing on a session nobody could renew; once the server answers again, the
//   same button keeps the session and closes it.
// Type: Functional (exceptional condition)
// Requirements: proposed FR-63
// Use case: UC 11, Sign in with multi-factor authentication
// Preconditions: A clinician signed in on the on-site app, on the quick texts page. Chromium, its clock installed, the
//   server's cookie clock moved on with it.
// Inputs and system conditions: 13 minutes with nothing done; the session page's requests refused by the network; Enter
//   on "Stay signed in"; the network back; Enter again.
// Test data: None.
// Steps:
//   1. Leave the page 13 minutes.
//   2. With the session page unreachable, press Enter on "Stay signed in".
//   3. With it reachable again, press Enter.
// Expected result: Step 1: the warning opens. Step 2: it stays open, saying the server can't be reached. Step 3: it
//   closes, and the server gives the session its 15 minutes again.
// Creator: Tahseen Arnan
[Collection(BrowserTests.Collection)]
public class TC_UC11_15_StayWithServerUnreachable(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Stay_signed_in_with_the_server_unreachable_keeps_the_warning_open_and_says_so()
    {
        var session = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Dr. Offline Stay", StaffRoles.Clinician));
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{server.Url}/quick-texts");

        // 1.
        await IdleAsync(apps, page, TimeSpan.FromMinutes(13));
        await page.WaitForSelectorAsync(OpenWarning);
        Assert.False(await page.IsVisibleAsync("[data-session-unreachable]"));

        // 2.
        await page.RouteAsync("**/session", route => route.AbortAsync());
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForSelectorAsync("[data-session-unreachable]", new() { State = WaitForSelectorState.Visible });
        Assert.Contains("The server can't be reached", await page.TextContentAsync("[data-session-unreachable]"));
        Assert.True(await page.IsVisibleAsync(OpenWarning));

        // 3.
        await page.UnrouteAsync("**/session");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForSelectorAsync(OpenWarning, new() { State = WaitForSelectorState.Detached });
        Assert.InRange(await LeftAsync(page), 890, 900);
    }
}
