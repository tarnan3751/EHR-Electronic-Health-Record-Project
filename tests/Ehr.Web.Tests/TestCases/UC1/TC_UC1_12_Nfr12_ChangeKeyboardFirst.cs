using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC1;

// Test Case ID: TC-UC1-12
// Purpose / scenario: Discontinuing and changing an order are keyboard-first: from the chart to the saved change with
//   no mouse, in a real browser, with the cursor starting where the typing starts.
// Type: Non-functional (usability)
// Requirements: NFR-12
// Use case: UC 1, Place an order (changing a signed order)
// Preconditions: The clinician is signed in on the on-site app and the patient is on their panel. The patient has two
//   active orders. The clinic's server is up. Chromium, driven by Playwright, with the keyboard only.
// Inputs and system conditions: Key presses only: Tab, Enter, select-all and typing.
// Test data: Patient Synthetic Orderable. Orders "Complete blood count (CBC)" and "Metformin 500 mg tablet" (Dose
//   "500 mg", Route "By mouth", Frequency "Twice a day"). Reason "Synthetic: keyboard only". New dose "850 mg".
// Steps:
//   1. Open the chart. Press Tab until the CBC order's "Discontinue…" link has the focus, and press Enter.
//   2. Type the reason, press Tab, and press Enter on "Discontinue order".
//   3. On the chart, press Tab until the Metformin order's "Change" link has the focus, and press Enter.
//   4. Press Tab to the dose, replace it, and press Enter.
// Expected result: Step 1 opens the discontinue page with the cursor in the reason field. Step 2 returns to the chart,
//   which says "Order discontinued:" and no longer lists the CBC order. Step 3 opens the change form with the cursor
//   in the item, filled in. Step 4 returns to the chart, which says "Order changed:" and lists Metformin with dose
//   850 mg. Every focused control shows the focus ring (an outline), and the page reports no script errors.
// Creator: Tahseen Arnan
//
// In the browser tests' collection, which doesn't run beside the others (AiReviewBrowserTests says why).
[Collection(BrowserTests.Collection)]
public class TC_UC1_12_Nfr12_ChangeKeyboardFirst(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Discontinuing_and_changing_an_order_work_from_the_keyboard()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        await SignedAsync(session.Client, patient, "medication", "Metformin 500 mg tablet", [("dose", "500 mg"), ("route", "By mouth"), ("frequency", "Twice a day")]);
        var cbc = await SignedAsync(session.Client, patient, "lab", "Complete blood count (CBC)", Cbc);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await AiReviewBrowserTests.ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        const string focusRing = "getComputedStyle(document.activeElement).outlineStyle !== 'none'";

        await page.GotoAsync($"{server.Url}/patients/{patient}");
        await AiReviewBrowserTests.TabToAsync(page, $"a[data-discontinue-order][href*='{cbc}']");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/discontinue");
        // autofocus is applied after the page loads, at the browser's next chance to render: wait for it rather than
        // checking at the instant of the load event, which a busy runner can reach first.
        await page.WaitForFunctionAsync("document.activeElement?.matches('#discontinue-reason') === true");
        await page.Keyboard.InsertTextAsync("Synthetic: keyboard only");
        await page.Keyboard.PressAsync("Tab");
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('form[data-order-form] button[type=submit]')"));
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync(url => url.Contains("discontinued=", StringComparison.Ordinal));
        Assert.Contains("Order discontinued:", await page.Locator(".sc-notice--success").TextContentAsync());
        Assert.Equal(0, await page.Locator($"a[href*='{cbc}']").CountAsync());

        await AiReviewBrowserTests.TabToAsync(page, "a[data-change-order]");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync(url => url.Contains("replaces=", StringComparison.Ordinal));
        await page.WaitForFunctionAsync("document.activeElement?.matches('#order-item') === true");
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.value === 'Metformin 500 mg tablet'"));
        await page.Keyboard.PressAsync("Tab");
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('#order-dose')"));
        await page.Keyboard.PressAsync("ControlOrMeta+A");
        await page.Keyboard.InsertTextAsync("850 mg");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync(url => url.Contains("ordered=", StringComparison.Ordinal));
        Assert.Contains("Order changed:", await page.Locator(".sc-notice--success").TextContentAsync());
        Assert.Contains("Dose: 850 mg", await page.Locator("#chart-orders ~ ul").TextContentAsync());

        Assert.Equal(1, await ActiveAsync(apps, patient));
        Assert.Empty(errors);
    }
}
