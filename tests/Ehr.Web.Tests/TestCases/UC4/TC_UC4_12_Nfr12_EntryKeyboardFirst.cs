using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.ChartListTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-12
// Purpose / scenario: Adding a coded diagnosis is keyboard-first: from the chart to the saved entry with no mouse, in
//   a real browser, with the code search answering as the clinician types.
// Type: Non-functional (usability)
// Requirements: NFR-12
// Use case: UC 4, View a patient's chart (editing the chart's lists)
// Preconditions: The clinician is signed in on the on-site app and the patient is on their panel, with no diagnoses.
//   The reference codes are loaded. Chromium, driven by Playwright, with the keyboard only.
// Inputs and system conditions: Key presses only: Tab, Enter and typing.
// Test data: Patient Synthetic Listed. Typed: "essen", then the rest of "Essential (primary) hypertension".
// Steps:
//   1. Open the chart. Press Tab until "Add diagnosis" has the focus, and press Enter.
//   2. Type "essen" and wait for the suggestions.
//   3. Type the rest of the suggested name, press Tab until "Save diagnosis" has the focus, and press Enter.
// Expected result: Step 1 opens the form with the cursor in the diagnosis field. After step 2 the field's list offers
//   "Essential (primary) hypertension". Step 3 saves and returns to the chart, which lists the diagnosis with its code
//   I10. Every focused control shows the focus ring, and the page reports no script errors.
// Creator: Tahseen Arnan
//
// In the browser tests' collection, which doesn't run beside the others (AiReviewBrowserTests says why).
[Collection(BrowserTests.Collection)]
public class TC_UC4_12_Nfr12_EntryKeyboardFirst(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Adding_a_coded_diagnosis_works_from_the_keyboard()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await AiReviewBrowserTests.ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        const string focusRing = "getComputedStyle(document.activeElement).outlineStyle !== 'none'";

        await page.GotoAsync($"{server.Url}/patients/{patient}");
        await AiReviewBrowserTests.TabToAsync(page, "a[data-add-entry=diagnoses]");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/diagnoses");
        // Focus lands once the page has loaded: waited for, not looked at once (it failed now and then under load).
        await page.WaitForFunctionAsync("document.activeElement.matches('#entry-name')");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));

        await page.Keyboard.TypeAsync("essen");
        await page.WaitForFunctionAsync("[...document.querySelectorAll('#entry-codes option')].some(o => o.value === 'Essential (primary) hypertension')");
        await page.Keyboard.TypeAsync("tial (primary) hypertension");
        // Enter in the field would only close the browser's list of suggestions: Tab on to the button, as a clinician would.
        await AiReviewBrowserTests.TabToAsync(page, "form.sc-form button[type=submit]");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        try
        {
            await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == $"/patients/{patient}");
        }
        catch (TimeoutException)
        {
            // Say what the page was showing, so a failure on a slow runner explains itself.
            Assert.Fail($"The diagnosis wasn't saved. Page: {page.Url}. Main: {await page.Locator("main").InnerTextAsync()}");
        }

        Assert.Contains("Essential (primary) hypertension", await page.Locator("#chart-diagnoses ~ ul").TextContentAsync());

        Assert.Equal(["Essential (primary) hypertension I10"], await RowsAsync(apps, "conditions", patient, "concat_ws(' ', display, code)"));
        Assert.Empty(errors);
    }
}
