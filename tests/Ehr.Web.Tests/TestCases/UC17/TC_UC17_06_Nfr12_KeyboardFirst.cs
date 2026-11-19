using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.MessageTests;

namespace Ehr.Web.Tests.TestCases.UC17;

// Test Case ID: TC-UC17-06
// Purpose / scenario: Answering a patient's message is keyboard-first: from the inbox to the sent reply with no mouse,
//   in a real browser.
// Type: Non-functional (usability)
// Requirements: NFR-12
// Use case: UC 17, Answer a patient's message
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel; the patient has sent
//   a message. Chromium, driven by Playwright, with the keyboard only.
// Inputs and system conditions: Key presses only: Tab, Enter and typing.
// Test data: Reply "Synthetic: answered by keyboard".
// Steps:
//   1. Open the inbox. Press Tab until "Open the conversation" has the focus, and press Enter.
//   2. Type the reply, press Tab, and press Enter on "Send reply".
// Expected result: Step 1 opens the conversation with the cursor in the reply field. Step 2 sends the reply: the page
//   says "Sent:" and shows it, and the app bar's inbox count is gone. Every focused control shows the focus ring, and
//   the page reports no script errors.
// Creator: Tahseen Arnan
//
// In the browser tests' collection, which doesn't run beside the others (AiReviewBrowserTests says why).
[Collection(BrowserTests.Collection)]
public class TC_UC17_06_Nfr12_KeyboardFirst(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Answering_a_message_works_from_the_keyboard()
    {
        var (_, patient, staff, portal) = await CareAsync(apps, "Dr. Message Keys");
        await StartAsync(portal.Client, "Synthetic: keyboard", "Synthetic: can you answer?");
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await AiReviewBrowserTests.ContextAsync(browser, staff);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        const string focusRing = "getComputedStyle(document.activeElement).outlineStyle !== 'none'";

        await page.GotoAsync($"{server.Url}/inbox");
        await AiReviewBrowserTests.TabToAsync(page, "a[data-inbox-source]");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/messages/*");
        // Focus lands once the page has loaded: waited for, not looked at once (it failed now and then under load).
        await page.WaitForFunctionAsync("document.activeElement.matches('#message-body')");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));

        await page.Keyboard.TypeAsync("Synthetic: answered by keyboard");
        await page.Keyboard.PressAsync("Tab");
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('form.sc-form button[type=submit]')"));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync(url => url.Contains("sent=", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Sent:", await page.Locator(".sc-notice--success").TextContentAsync());
        Assert.Contains("Synthetic: answered by keyboard", await page.Locator(".sc-thread").TextContentAsync());
        Assert.Equal(0, await page.Locator("[data-inbox-count]").CountAsync());

        Assert.Equal(["Synthetic: can you answer?", "Synthetic: answered by keyboard"], await RowsAsync(apps, patient, "body"));
        Assert.Empty(errors);
    }
}
