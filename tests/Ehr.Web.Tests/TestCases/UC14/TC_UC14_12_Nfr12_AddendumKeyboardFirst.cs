using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-12
// Purpose / scenario: Adding an addendum is keyboard-first: from the chart to the signed addendum with no mouse, in a
//   real browser.
// Type: Non-functional (usability)
// Requirements: NFR-12
// Use case: UC 14, Review and sign a clinical note
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel and has signed a note.
//   Chromium, driven by Playwright, with the keyboard only.
// Inputs and system conditions: Key presses only: Tab, Enter and typing.
// Test data: "Synthetic keyboard addendum."
// Steps:
//   1. Open the chart. Press Tab until the signed note's link has the focus, and press Enter.
//   2. Press Tab until "Add an addendum" has the focus, and press Enter.
//   3. Type the text (the cursor starts in the field). Press Tab until "Sign addendum" has the focus, and press Enter.
// Expected result: Step 1 opens the signed note. Step 2 opens the addendum page with the cursor in the text field.
//   Step 3 signs it: the note's page says "Signed:" and lists the addendum. Every focused control shows the focus
//   ring, and the pages report no script errors.
// Creator: Tahseen Arnan
//
// In the browser tests' collection, which doesn't run beside the others (AiReviewBrowserTests says why).
[Collection(BrowserTests.Collection)]
public class TC_UC14_12_Nfr12_AddendumKeyboardFirst(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task An_addendum_is_written_and_signed_from_the_keyboard()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        var note = await SignedNoteAsync(session.Client, patient);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await AiReviewBrowserTests.ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        const string focusRing = "getComputedStyle(document.activeElement).outlineStyle !== 'none'";

        await page.GotoAsync($"{server.Url}/patients/{patient}");
        await AiReviewBrowserTests.TabToAsync(page, $"a[href='{NotePath(patient, note)}']");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync($"**{NotePath(patient, note)}");
        await AiReviewBrowserTests.TabToAsync(page, "a[data-add-addendum]");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/addendum");
        // Focus lands once the page has loaded: waited for, not looked at once (it failed now and then under load).
        await page.WaitForFunctionAsync("document.activeElement.matches('textarea[name=\"Text\"]')");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));

        await page.Keyboard.TypeAsync("Synthetic keyboard addendum.");
        await AiReviewBrowserTests.TabToAsync(page, "button[data-sign-addendum]");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync(url => url.Contains("done=addendum", StringComparison.Ordinal));
        Assert.Contains("Signed:", await page.Locator(".sc-notice--success").TextContentAsync());
        Assert.Contains("Synthetic keyboard addendum.", await page.Locator("[data-addenda]").TextContentAsync());

        Assert.Single(await AddendaAsync(apps, patient));
        Assert.Empty(errors);
    }
}
