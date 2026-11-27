using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-16
// Purpose / scenario: Marking a signed note sensitive is keyboard-first: from the note to the marked note with no
//   mouse, in a real browser.
// Type: Non-functional (usability)
// Requirements: NFR-12
// Use case: UC 14, Review and sign a clinical note
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel and has signed a note.
//   Chromium, driven by Playwright, with the keyboard only.
// Inputs and system conditions: Key presses only: Tab, Enter and typing.
// Test data: Reason "Patient asked for it."
// Steps:
//   1. Open the signed note. Press Tab until "Mark this note sensitive" has the focus, and press Enter.
//   2. Type the reason (the cursor starts in the field) and press Enter.
// Expected result: Step 1 opens the page with the cursor in the reason. Step 2 marks it: the note's page says
//   "Marked sensitive." and carries the "Sensitive" tag. Every focused control shows the focus ring, and the pages report no
//   script errors.
// Creator: Tahseen Arnan
//
// In the browser tests' collection, which doesn't run beside the others (AiReviewBrowserTests says why).
[Collection(BrowserTests.Collection)]
public class TC_UC14_16_Nfr12_SensitivityKeyboardFirst(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_signed_note_is_marked_sensitive_from_the_keyboard()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        var note = await SignedNoteAsync(session.Client, patient);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await AiReviewBrowserTests.ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        const string focusRing = "getComputedStyle(document.activeElement).outlineStyle !== 'none'";

        await page.GotoAsync($"{server.Url}{NotePath(patient, note)}");
        await AiReviewBrowserTests.TabToAsync(page, "a[data-note-sensitivity]");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/sensitivity");
        // Focus lands once the page has loaded: waited for, not looked at once (it failed now and then under load).
        await page.WaitForFunctionAsync("document.activeElement.matches('#sensitivity-reason')");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));

        await page.Keyboard.TypeAsync("Patient asked for it.");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync(url => url.Contains("done=marked", StringComparison.Ordinal));
        Assert.Contains("Marked sensitive.", await page.Locator(".sc-notice--success").TextContentAsync());
        Assert.Equal(1, await page.Locator("[data-note-sensitive]").CountAsync());

        Assert.Equal([$"note true {clinician.Id} Patient asked for it."], await SensitivityAsync(apps, patient));
        Assert.Empty(errors);
    }
}
