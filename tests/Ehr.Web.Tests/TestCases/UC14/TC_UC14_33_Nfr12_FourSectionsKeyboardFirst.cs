using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VitalTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-33
// Purpose / scenario: In a real browser, with the keyboard only, a clinician writes a note in its four sections: picks
//   a visit type and reads its prompts, has the vital signs offered and adds them to Objective, saves with Ctrl+S (Cmd+S
//   on a Mac), and signs.
// Type: Non-functional (usability: keyboard first)
// Requirements: NFR-12, proposed FR-60
// Use case: UC 14, Review and sign a clinical note
// Preconditions: Playwright's Chromium is installed (CI installs it). The clinician is signed in with the patient on
//   their panel, and a heart rate of 72 was recorded a minute ago by the database's clock.
// Inputs and system conditions: the chart, the note editor and the confirmation, in Chromium, through the keyboard.
// Test data: the visit type "Follow-up visit"; "Synthetic keyboard subjective", "Synthetic keyboard assessment" and
//   "Synthetic keyboard plan".
// Steps:
//   1. From the chart, reach "New note" and press Enter.
//   2. Shift+Tab to "Visit type" and type "F" to choose "Follow-up visit".
//   3. Type Subjective; Tab to Objective, then to "Add vital signs to Objective…", and press Enter; press Enter on "Add to
//      Objective".
//   4. Type Assessment and Plan, and at once press Ctrl+S (Cmd+S on a Mac).
//   5. Reach "Sign note…", confirm and sign.
// Expected result: Step 1: the cursor is in Subjective. Step 2: each section shows its follow-up prompt. Step 3: the
//   offer names the heart rate and the focus is on "Add to Objective"; once added, the text is in Objective, after
//   nothing else, the focus is back in Objective, and a status says to check it. Step 4: the draft is saved through "Save
//   draft" (its buttons are disabled while the form is sent, which saving after a pause in typing doesn't do), and the
//   status says "Saved". Step 5: the signed note shows the four sections with the vital signs in Objective and no
//   prompt. Every focused control shows the focus ring, and the pages report no script errors.
// Creator: Tahseen Arnan
//
// In the browser tests' collection, which doesn't run beside the others (AiReviewBrowserTests says why).
[Collection(BrowserTests.Collection)]
public class TC_UC14_33_Nfr12_FourSectionsKeyboardFirst(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_note_is_written_with_a_visit_type_and_vital_signs_and_signed_from_the_keyboard()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        using (var recorded = await RecordAsync(apps, session.Client, patient, 1, (HeartRate, "72")))
        {
            Assert.Contains("Saved.", await ReadAsync(recorded));
        }

        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await AiReviewBrowserTests.ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        const string focusRing = "getComputedStyle(document.activeElement).outlineStyle !== 'none'";

        await page.GotoAsync($"{server.Url}/patients/{patient}");
        await AiReviewBrowserTests.TabToAsync(page, "form[action*='handler=NewNote'] button");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/notes/drafts/**");
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('#Subjective')"));

        for (var i = 0; i < 10 && !await page.EvaluateAsync<bool>("document.activeElement.matches('#NoteTemplateCode')"); i++)
        {
            await page.Keyboard.PressAsync("Shift+Tab");
        }

        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        // Typed: the first letter chooses the visit type on every platform (an arrow key opens the list on a Mac).
        await page.Keyboard.PressAsync("F");
        await page.WaitForFunctionAsync("!document.getElementById('note-prompt-objective').hidden");
        Assert.StartsWith("Follow-up visit: ", await page.Locator("#note-prompt-subjective").TextContentAsync());

        await AiReviewBrowserTests.TabToAsync(page, "#Subjective");
        await page.Keyboard.InsertTextAsync("Synthetic keyboard subjective");
        await AiReviewBrowserTests.TabToAsync(page, "#Objective");
        await AiReviewBrowserTests.TabToAsync(page, "[data-note-vitals]");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync("document.activeElement.matches('[data-note-vitals-add]')");
        Assert.Contains("heart rate 72 per minute", await page.Locator("[data-note-vitals-text]").TextContentAsync());
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        var objective = await page.Locator("#Objective").InputValueAsync();
        Assert.StartsWith("Vital signs, ", objective);
        Assert.Contains("heart rate 72 per minute", objective);
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('#Objective')"));
        Assert.Equal("Vital signs added to Objective.", await page.Locator("[data-note-vitals-status]").TextContentAsync());

        await AiReviewBrowserTests.TabToAsync(page, "#Assessment");
        await page.Keyboard.InsertTextAsync("Synthetic keyboard assessment");
        await AiReviewBrowserTests.TabToAsync(page, "#Plan");
        await page.Keyboard.InsertTextAsync("Synthetic keyboard plan");
        // Ctrl+S sends the form through "Save draft", which disables its buttons while it is sent; saving after a pause
        // in typing leaves them alone. Watched from before the key is pressed, so a save by either way is told apart.
        await page.EvaluateAsync("""
            () => {
                window.sentByKey = false;
                new MutationObserver(() => { window.sentByKey = true; })
                    .observe(document.querySelector('[data-note-save]'), { attributes: true, attributeFilter: ['disabled'] });
            }
            """);
        await page.Keyboard.PressAsync("ControlOrMeta+S");
        await page.WaitForFunctionAsync("document.getElementById('note-save-status').textContent === 'Saved'");
        Assert.True(await page.EvaluateAsync<bool>("window.sentByKey"));
        await AiReviewBrowserTests.TabToAsync(page, "[data-review]");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/confirm?version=*");
        await AiReviewBrowserTests.TabToAsync(page, "[name=Confirmed][type=checkbox]");
        await page.Keyboard.PressAsync("Space");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/notes/*?signed=True");

        var signed = await page.Locator("main").TextContentAsync();
        Assert.Contains("Synthetic keyboard subjective", signed);
        Assert.Contains("heart rate 72 per minute", signed);
        Assert.DoesNotContain("Follow-up visit", signed);
        Assert.Empty(errors);
    }
}
