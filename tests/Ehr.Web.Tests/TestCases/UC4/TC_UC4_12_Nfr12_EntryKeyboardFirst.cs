using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.ChartListTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-12
// Purpose / scenario: Adding a coded diagnosis is keyboard-first: from the chart to the saved entry with no mouse, in
//   a real browser, with the code search answering as the clinician types; a name that isn't a code brings the form
//   back with the cursor in that field; and after saving, the focus is on the chart's "Saved:" notice. (Picking a
//   suggestion with the arrow keys can't be automated: headless Chromium draws the browser's own list outside the
//   page. It is checked by hand.)
// Type: Non-functional (usability)
// Requirements: NFR-12
// Use case: UC 4, View a patient's chart (editing the chart's lists)
// Preconditions: The clinician is signed in on the on-site app and the patient is on their panel, with no diagnoses.
//   The reference codes are loaded. Chromium, driven by Playwright, with the keyboard only.
// Inputs and system conditions: Key presses only: Tab, Enter and typing.
// Test data: Patient Synthetic Listed. Typed: "zzqq" (no such code), then "essen", then the rest of "Essential
//   (primary) hypertension".
// Steps:
//   1. Open the chart. Press Tab until "Add diagnosis" has the focus, and press Enter.
//   2. Type "zzqq", press Tab until "Save diagnosis" has the focus, and press Enter.
//   3. Clear the field, type "essen" and wait for the suggestions; type the rest of the suggested name.
//   4. Press Tab until "Save diagnosis" has the focus, and press Enter.
// Expected result: Step 1 opens the form with the cursor in the diagnosis field. In step 2 the search says "No
//   matches.", and saving brings the form back with "Choose a diagnosis from the list." and the cursor in the
//   diagnosis field, marked invalid. In step 3 the field's
//   list offers "Essential (primary) hypertension". Step 4 saves and returns to the
//   chart, which lists the diagnosis with its code I10, with the focus on "Saved: Essential (primary) hypertension is
//   listed under Diagnoses." Every focused control shows the focus ring, and the page reports no script errors.
// Creator: Tahseen Arnan (expansion step 2), UC 4 owner Diego Valenzuela
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
        await page.WaitForFunctionAsync("document.activeElement?.matches('#entry-name') === true");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));

        // A name that isn't a code: the form comes back with the cursor in the field, marked invalid.
        await page.Keyboard.TypeAsync("zzqq");
        // The search says so, in the field's live region, when nothing matches.
        await page.WaitForFunctionAsync("document.querySelector('[data-code-search-status]').textContent.includes('No matches.')");
        await AiReviewBrowserTests.TabToAsync(page, "form.sc-form button[type=submit]");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForSelectorAsync("#entry-name-error");
        Assert.Contains("Choose a diagnosis from the list.", await page.Locator("#entry-name-error").TextContentAsync());
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('#entry-name[aria-invalid=true]')"));
        Assert.True(await page.EvaluateAsync<bool>(focusRing));

        // The suggestions are the browser's own list (a datalist), which headless Chromium draws outside the page, where
        // Playwright's key presses don't reach: the arrow keys and Enter can't pick from it here. So the name is typed
        // in full once the list offers it, as a clinician may; picking with the arrows is checked by hand.
        await page.Keyboard.PressAsync("Control+A");
        await page.Keyboard.PressAsync("Backspace");
        await page.Keyboard.TypeAsync("essen");
        await page.WaitForFunctionAsync("[...document.querySelectorAll('#entry-codes option')].some(o => o.value === 'Essential (primary) hypertension')");
        await page.Keyboard.TypeAsync("tial (primary) hypertension");
        Assert.Equal("Essential (primary) hypertension", await page.InputValueAsync("#entry-name"), ignoreCase: true);
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
        // The chart's notice has the focus, so the keyboard and a screen reader start from what was saved.
        await page.WaitForFunctionAsync("document.activeElement?.matches('[data-saved]') === true");
        Assert.Contains("Saved: Essential (primary) hypertension is listed under Diagnoses.", await page.EvaluateAsync<string>("document.activeElement.textContent"));

        Assert.Equal(["Essential (primary) hypertension I10"], await RowsAsync(apps, "conditions", patient, "concat_ws(' ', display, code)"));
        Assert.Empty(errors);
    }
}
