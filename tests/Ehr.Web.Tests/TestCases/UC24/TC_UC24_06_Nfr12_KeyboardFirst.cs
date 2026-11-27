using Ehr.Data.Accounts;
using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.EmergencyAccessFlows;

namespace Ehr.Web.Tests.TestCases.UC24;

// Test Case ID: TC-UC24-06
// Purpose / scenario: An administrator reviews an emergency access from the keyboard alone, from the inbox to the saved
//   review, in a real browser (the expansion plan's step 18).
// Type: Non-functional (usability)
// Requirements: NFR-12, proposed FR-64
// Use case: UC 24, Review an emergency access
// Preconditions: Dr. Keyboard Access used emergency access, now ended; the administrator Keyboard Reviewer is signed in
//   on the on-site app. Chromium, driven by Playwright, with the keyboard only.
// Inputs and system conditions: Key presses only: Tab, Space, typing and Enter.
// Test data: Note "Synthetic: checked the rota, the clinician was covering the ward."
// Steps:
//   1. Open the inbox; press Tab until "Review the access" has the focus; press Enter.
//   2. Press Space on "Appropriate", which has the focus; Tab to the note and type it; Tab to "Mark reviewed"; Enter.
// Expected result: Step 1 opens the access's review page with the focus on "Appropriate". Step 2 saves the review: the
//   page says "Reviewed.". Every focused control shows the focus ring, and the page reports no script errors.
// Creator: Tahseen Arnan
[Collection(BrowserTests.Collection)]
public class TC_UC24_06_Nfr12_KeyboardFirst(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task An_access_is_reviewed_from_the_keyboard()
    {
        var reviewer = await apps.CreateStaffAsync("Keyboard Reviewer", StaffRoles.Admin);
        var access = await GrantAsync(apps, "Dr. Keyboard Access");
        await EndAsync(apps, access.Id);
        var session = await SignInAsync(apps.OnPrem, reviewer);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await AiReviewBrowserTests.ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        const string focusRing = "getComputedStyle(document.activeElement).outlineStyle !== 'none'";

        // 1.
        await page.GotoAsync($"{server.Url}/inbox");
        await AiReviewBrowserTests.TabToAsync(page, $"a[href='/admin/emergency-access/{access.Id}']");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync($"**/admin/emergency-access/{access.Id}");
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('input[name=Outcome][value=appropriate]')"));

        // 2.
        await page.Keyboard.PressAsync("Space");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await AiReviewBrowserTests.TabToAsync(page, "#review-note");
        await page.Keyboard.TypeAsync(Note);
        await AiReviewBrowserTests.TabToAsync(page, "[data-review-form] button");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForSelectorAsync("[data-review-saved]");
        Assert.Empty(errors);
    }
}
