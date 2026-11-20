using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VitalTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-22
// Purpose / scenario: Marking a reading entered in error is keyboard-first: from the flowsheet to the marked reading
//   with no mouse, in a real browser.
// Type: Non-functional (usability)
// Requirements: NFR-12
// Use case: UC 4, View patient chart
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel, who has one set of
//   vital signs (blood pressure 180/92). Chromium, driven by Playwright, with the keyboard only.
// Inputs and system conditions: Key presses only: Tab, Space, Enter and typing.
// Test data: Reason "Typed 180 for 108."
// Steps:
//   1. Open the vital signs page. Press Tab until "Mark in error" has the focus, and press Enter.
//   2. Press Space (the cursor starts on the first reading, the blood pressure). Press Tab until the reason has the
//      focus, type it, and press Enter.
// Expected result: Step 1 opens the page with the first reading's box focused. Step 2 marks it: the vital signs page
//   says "Marked:" and the blood pressure, both its numbers (step 9c), is entered in error in the record. Every
//   focused control shows the focus ring, and the pages report no script errors.
// Creator: Tahseen Arnan
//
// In the browser tests' collection, which doesn't run beside the others (AiReviewBrowserTests says why).
[Collection(BrowserTests.Collection)]
public class TC_UC4_22_Nfr12_VitalSignErrorKeyboardFirst(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_reading_is_marked_entered_in_error_from_the_keyboard()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        using var recorded = await RecordAsync(session.Client, patient, 30, (Systolic, "180"), (Diastolic, "92"));
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await AiReviewBrowserTests.ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        const string focusRing = "getComputedStyle(document.activeElement).outlineStyle !== 'none'";

        await page.GotoAsync($"{server.Url}{Path(patient)}");
        await AiReviewBrowserTests.TabToAsync(page, "a[data-mark-in-error]");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/vitals/error?at=*");
        // Focus lands once the page has loaded: waited for, not looked at once (it failed now and then under load).
        await page.WaitForFunctionAsync("document.activeElement.matches('input[name=\"Chosen\"]')");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));

        await page.Keyboard.PressAsync("Space");
        await AiReviewBrowserTests.TabToAsync(page, "#error-reason");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.TypeAsync("Typed 180 for 108.");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync(url => url.Contains("marked=1", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Marked:", await page.Locator(".sc-notice--success").TextContentAsync());

        Assert.Equal([$"{Diastolic} 92 entered-in-error", $"{Systolic} 180 entered-in-error"], await StatesAsync(apps, patient));
        Assert.Empty(errors);
    }
}
