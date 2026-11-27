using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.SafetyTests;

namespace Ehr.Web.Tests.TestCases.UC1;

// Test Case ID: TC-UC1-20
// Purpose / scenario: Answering a safety warning is keyboard-first: when an order is stopped, the cursor is in the
//   reason field, and the order is signed without the mouse, in a real browser.
// Type: Non-functional (usability)
// Requirements: NFR-12
// Use case: UC 1, Place an order
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel; the patient has a
//   coded allergy to amoxicillin. Chromium, driven by Playwright, with the keyboard only.
// Inputs and system conditions: Key presses only: typing, Tab and Enter.
// Test data: "Amoxicillin 500 mg capsule", 500 mg, By mouth, Three times a day. Reason "Synthetic: tolerated since".
// Steps:
//   1. Open the medication order form. Type the item, press Tab, and type the dose, route and frequency, pressing Tab
//      between them. Press Tab until "Sign order" has the focus, and press Enter.
//   2. Type the reason (the cursor is already in its field). Press Tab until "Sign order" has the focus, and press
//      Enter.
// Expected result: Step 1: the form stays, shows "Check before signing" with the allergy, and the cursor is in "Override
//   reason", which shows the focus ring. Step 2: the order is signed and the chart opens.
//   The page reports no script errors.
// Creator: Tahseen Arnan
//
// In the browser tests' collection, which doesn't run beside the others (AiReviewBrowserTests says why).
[Collection(BrowserTests.Collection)]
public class TC_UC1_20_Nfr12_SafetyKeyboardFirst(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_safety_warning_is_answered_from_the_keyboard()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        await AllergyAsync(apps, patient, "amoxicillin");
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await AiReviewBrowserTests.ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);

        await page.GotoAsync($"{server.Url}{FormPath(patient, "medication")}");
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('input[name=Item]')"));
        foreach (var value in new[] { "Amoxicillin 500 mg capsule", "500 mg", "By mouth", "Three times a day" })
        {
            await page.Keyboard.TypeAsync(value);
            await page.Keyboard.PressAsync("Tab");
        }

        await AiReviewBrowserTests.TabToAsync(page, "form[data-order-form] button[type=submit]");
        await page.Keyboard.PressAsync("Enter");
        await page.Locator("[data-order-safety=reason]").WaitForAsync();
        Assert.Contains("Allergy: Amoxicillin", await page.Locator("[data-order-safety]").TextContentAsync());
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('#order-safety-reason')"));
        Assert.True(await page.EvaluateAsync<bool>("getComputedStyle(document.activeElement).outlineStyle !== 'none'"));

        await page.Keyboard.TypeAsync("Synthetic: tolerated since");
        await AiReviewBrowserTests.TabToAsync(page, "form[data-order-form] button[type=submit]");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync(url => url.Contains("ordered=", StringComparison.Ordinal));

        Assert.Equal(["Synthetic: tolerated since|allergy:major:chart"], await OrdersAsync(apps, patient));
        Assert.Empty(errors);
    }
}
