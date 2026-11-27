using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.PreVisitFormTests;

namespace Ehr.Web.Tests.TestCases.UC22;

// Test Case ID: TC-UC22-11
// Purpose / scenario: A form can be filled in on a phone, and from the keyboard alone: at 360 pixels wide nothing
//   scrolls sideways, in the light theme or the dark; each question is a group named by the question, each answer at
//   least 44 pixels tall; Tab reaches each question's answers, the arrow keys choose among them with the focus ring
//   showing, and Enter on the button sends the form. Once sent, its answers fit any width: at 1,280, 640 and 360
//   pixels, in both themes, the patient's page and the clinician's have nothing wider than the window, and each answer
//   sits under its question, at least half the list's width.
// Type: Non-functional (usability)
// Requirements: NFR-12, proposed FR-59
// Use case: UC 22, Answer a pre-visit form
// Preconditions: The clinician has the patient on their panel, an appointment two days ahead and the GAD-7 asked for
//   before it. The patient is signed in to the portal. Chromium, driven by Playwright, 360 by 740 pixels.
// Inputs and system conditions: GET /portal/forms/{form}; key presses only: Tab, the arrow keys and Enter. For the
//   answers, a PHQ-9 answered as the patient's account, GET /portal/forms/{form} as the patient and GET
//   /patients/{id}/forms/{answers} as the clinician, in windows 1,280, 640 and 360 pixels wide.
// Test data: Clinician "Dr. Forms Eleven". The second answer, "Several days", to each of the seven questions.
// Steps:
//   1. Open the form in the light theme and in the dark.
//   2. For each question, press Tab until its first answer has the focus, then the down arrow.
//   3. Press Tab until "Send to my care team" has the focus, and press Enter.
// Expected result: Step 1: no sideways scrolling in either theme; seven groups, each named by its question; every
//   answer at least 44 pixels tall. Step 2: each down arrow chooses "Several days", the focus ring showing on it. Step
//   3: the page says "Sent.", and the answers are stored with the database's score of 7. No script errors.
//   The answers, on both pages, at every width and in both themes: no sideways scrolling and no element wider than the
//   window; ten questions, each answer beneath its question, at its left edge, at least half the list's width.
// Creator: Tahseen Arnan
//
// In the browser tests' collection, which doesn't run beside the others (AiReviewBrowserTests says why).
[Collection(BrowserTests.Collection)]
public class TC_UC22_11_Nfr12_FormOnAPhone(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task At_360_pixels_a_form_fits_and_is_filled_in_and_sent_with_the_keyboard()
    {
        var visit = await VisitAsync(apps, "Dr. Forms Eleven");
        using (var asked = await AskAsync(visit.Session.Client, visit.Patient, visit.Appointment, Gad7))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, asked.StatusCode);
        }

        var request = await RequestAsync(apps, visit.Appointment, Gad7);
        var (_, jar) = await PortalAsync(apps, visit);

        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        var errors = new List<string>();
        const string fits = "document.documentElement.scrollWidth <= window.innerWidth";
        const string focusRing = "getComputedStyle(document.activeElement).outlineStyle !== 'none'";
        IPage? page = null;
        foreach (var scheme in new[] { ColorScheme.Dark, ColorScheme.Light })
        {
            var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ColorScheme = scheme, ViewportSize = new() { Width = 360, Height = 740 } });
            await context.AddCookiesAsync(jar.GetCookies(Base).Cast<System.Net.Cookie>().Select(c => new Microsoft.Playwright.Cookie
            { Name = c.Name, Value = c.Value, Domain = "localhost", Path = c.Path, Secure = true, HttpOnly = c.HttpOnly, SameSite = SameSiteAttribute.Strict }));
            page = await context.NewPageAsync();
            page.PageError += (_, error) => errors.Add(error);
            await page.GotoAsync($"{server.Url}{FormPath(request)}");
            Assert.True(await page.EvaluateAsync<bool>(fits), $"The form scrolls sideways at 360 pixels in the {scheme} theme.");
            Assert.Equal(7, await page.Locator("fieldset.sc-choice").CountAsync());
            Assert.Equal(1, await page.GetByRole(AriaRole.Group, new() { Name = "Trouble relaxing", Exact = true }).CountAsync());
            Assert.True(await page.EvaluateAsync<bool>("[...document.querySelectorAll('.sc-choice__option')].every(option => option.getBoundingClientRect().height >= 44)"),
                "An answer is less than 44 pixels tall.");
        }

        // The light theme's page, from the keyboard alone.
        foreach (var item in GadItems)
        {
            await AiReviewBrowserTests.TabToAsync(page!, $"#question-{item}-0");
            await page!.Keyboard.PressAsync("ArrowDown");
            Assert.Equal($"question-{item}-1", await page.EvaluateAsync<string>("document.activeElement.id"));
            Assert.True(await page.EvaluateAsync<bool>("document.activeElement.checked"));
            Assert.True(await page.EvaluateAsync<bool>(focusRing));
        }

        await AiReviewBrowserTests.TabToAsync(page!, "button.sc-btn--primary");
        Assert.Equal("Send to my care team", (await page!.EvaluateAsync<string>("document.activeElement.textContent")).Trim());
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync(url => url.Contains("sent=true", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Sent.", await page.Locator("main").TextContentAsync());
        Assert.Equal(["7"], await QueryAsync(apps, "SELECT score::text FROM ehr.questionnaire_responses WHERE request_id = @request", ("request", request)));
        Assert.Empty(errors);
    }

    // A form's answers, as the patient reads them and as the clinician does, at a desktop's width, a tablet's and a
    // phone's, in both themes: nothing wider than the window, and each answer under its question, at the question's
    // left edge, at least half the list's width (the review's walk found the answers squeezed into a sliver at the
    // right of a page 1,716 pixels wide in a 1,280-pixel window).
    [Fact]
    public async Task A_forms_answers_sit_under_their_questions_at_every_width_for_the_patient_and_the_clinician()
    {
        var visit = await VisitAsync(apps, "Dr. Forms Eleven Answers");
        using (var asked = await AskAsync(visit.Session.Client, visit.Patient, visit.Appointment, Phq9))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, asked.StatusCode);
        }

        var request = await RequestAsync(apps, visit.Appointment, Phq9);
        var answers = Answers(PhqItems, 2, 2, 2, 0, 0, 0, 0, 0, 1);
        answers["69722-7"] = "LA6573-5";
        var response = await AnswerAsync(apps, visit.Account, request, answers);
        var (_, jar) = await PortalAsync(apps, visit);

        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        var errors = new List<string>();
        const string Layout = """
            () => {
                const width = window.innerWidth;
                const list = document.querySelector('[data-form-answers]');
                const wide = [...document.querySelectorAll('body *')].filter(e => e.getBoundingClientRect().width > width).map(e => e.tagName + '.' + e.className);
                const pairs = [...list.querySelectorAll(':scope > dt')].map(dt => {
                    const q = dt.getBoundingClientRect();
                    const dd = dt.nextElementSibling;
                    const a = dd.getBoundingClientRect();
                    return dd.tagName === 'DD' && a.top >= q.bottom - 1 && Math.abs(a.left - q.left) <= 1 && a.width >= list.getBoundingClientRect().width / 2
                        && a.right <= width;
                });
                return { scrolls: document.documentElement.scrollWidth > width, wide, questions: pairs.length, stacked: pairs.every(ok => ok) };
            }
            """;
        foreach (var (who, path, cookies) in new[] { ("patient", FormPath(request), jar), ("clinician", $"{FormsPath(visit.Patient)}/{response}", visit.Session.Jar) })
        {
            foreach (var scheme in new[] { ColorScheme.Light, ColorScheme.Dark })
            {
                foreach (var width in new[] { 1280, 640, 360 })
                {
                    var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ColorScheme = scheme, ViewportSize = new() { Width = width, Height = 800 } });
                    await context.AddCookiesAsync(cookies.GetCookies(Base).Cast<System.Net.Cookie>().Select(c => new Microsoft.Playwright.Cookie
                    { Name = c.Name, Value = c.Value, Domain = "localhost", Path = c.Path, Secure = true, HttpOnly = c.HttpOnly, SameSite = SameSiteAttribute.Strict }));
                    var page = await context.NewPageAsync();
                    page.PageError += (_, error) => errors.Add(error);
                    await page.GotoAsync($"{server.Url}{path}");
                    var layout = await page.EvaluateAsync<System.Text.Json.JsonElement>(Layout);
                    var where = $"the {who}'s answers at {width} pixels in the {scheme} theme";
                    Assert.False(layout.GetProperty("scrolls").GetBoolean(), $"The page scrolls sideways: {where}.");
                    Assert.Equal(0, layout.GetProperty("wide").GetArrayLength());
                    Assert.Equal(10, layout.GetProperty("questions").GetInt32());
                    Assert.True(layout.GetProperty("stacked").GetBoolean(), $"An answer isn't under its question, or is narrow: {where}.");
                    await context.CloseAsync();
                }
            }
        }

        Assert.Empty(errors);
    }
}
