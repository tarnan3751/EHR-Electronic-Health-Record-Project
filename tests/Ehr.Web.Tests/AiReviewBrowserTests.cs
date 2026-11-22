using Ehr.Data.Audit;
using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Real DOM, focus and fetch behavior with deterministic synthetic audio/model results; no real microphone or models.
//
// In a collection that doesn't run beside the others. The other test classes start and stop PostgreSQL containers,
// and on Linux each one adds or removes a network interface on the machine. Chromium notices, and aborts whatever it
// is loading or sending with net::ERR_NETWORK_CHANGED: on CI that failed these tests at a different step each run
// (a page that wouldn't load, a save that never arrived), though never on a Mac, where Docker's interfaces are inside
// a virtual machine. Run after the parallel classes have finished, nothing changes the network under the browser.
[Collection(BrowserTests.Collection)]
public class AiReviewBrowserTests(WebApps apps) : IClassFixture<WebApps>
{
    const string FakeRecorder = """
        Object.defineProperty(navigator, 'mediaDevices', { value: { getUserMedia: async () => ({ getTracks: () => [{ stop() {} }] }) } });
        window.MediaRecorder = class extends EventTarget {
            constructor() { super(); this.state = 'inactive'; this.mimeType = 'audio/webm'; }
            start() {
                this.state = 'recording';
                for (let i = 0; i < 5; i++) setTimeout(() => {
                    if (this.state === 'recording') this.dispatchEvent(new BlobEvent('dataavailable', { data: new Blob(['synthetic audio'], { type: this.mimeType }) }));
                }, 20 + i * 50);
            }
            stop() { this.state = 'inactive'; this.dispatchEvent(new Event('stop')); }
        };
        """;

    async Task<(Guid Patient, Session Session)> AccountAsync()
    {
        apps.Dictation.Reset().IncludePlan = true;
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Synthetic", "Keyboard review");
        await apps.AssignAsync(clinician, patient);
        return (patient, await SignInAsync(apps.OnPrem, clinician));
    }

    internal static async Task<IBrowserContext> ContextAsync(IBrowser browser, Session session)
    {
        var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        await context.AddCookiesAsync(session.Jar.GetCookies(Base).Cast<System.Net.Cookie>().Select(c => new Microsoft.Playwright.Cookie
        { Name = c.Name, Value = c.Value, Domain = "localhost", Path = c.Path, Secure = true, HttpOnly = c.HttpOnly, SameSite = SameSiteAttribute.Strict }));
        await context.AddInitScriptAsync(FakeRecorder);
        return context;
    }

    // On a machine without Playwright's Chromium, these tests are skipped and say how to install it, so `dotnet test`
    // still passes for everyone. In CI, which installs it (ci.yml), a missing browser fails them: they must not be
    // skipped there unnoticed.
    internal static async Task<IBrowser> LaunchAsync(IPlaywright playwright)
    {
        try
        {
            return await playwright.Chromium.LaunchAsync(new()
            {
                Headless = true,
                ExecutablePath = Environment.GetEnvironmentVariable("PLAYWRIGHT_CHROMIUM_EXECUTABLE"),
            });
        }
        catch (PlaywrightException error) when (error.Message.Contains("Executable doesn't exist", StringComparison.Ordinal)
            && !string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("Not run: Playwright's Chromium isn't installed on this machine. After a Release build, install it with: "
                + "pwsh tests/Ehr.Web.Tests/bin/Release/net10.0/playwright.ps1 install chromium");
            throw;
        }
    }

    static async Task StartAndReviewAsync(IPage page, string url)
    {
        await page.GotoAsync(url);
        await page.Keyboard.PressAsync("Alt+Shift+D");
        await page.WaitForFunctionAsync("document.querySelector('[data-dictation-transcript]').textContent.includes('Fluids and rest.')");
        await page.Keyboard.PressAsync("Alt+Shift+D");
        try
        {
            await page.WaitForURLAsync("**/review/*");
        }
        catch (TimeoutException)
        {
            // Say what the page was showing, so a failure on a slow runner explains itself.
            var status = await page.Locator("[data-dictation-status]").TextContentAsync();
            var transcript = await page.Locator("[data-dictation-transcript]").TextContentAsync();
            Assert.Fail($"The dictation never reached its review. Page: {page.Url}. Status: \"{status}\". Transcript: \"{transcript}\".");
        }

        await page.WaitForFunctionAsync("document.querySelector('[data-review-progress]').textContent.includes('0 of 3')");
    }

    internal static async Task TabToAsync(IPage page, string selector)
    {
        for (var i = 0; i < 40; i++)
        {
            if (await page.EvaluateAsync<bool>("selector => document.activeElement.matches(selector)", selector)) return;
            await page.Keyboard.PressAsync("Tab");
        }
        Assert.Fail("Keyboard could not reach " + selector);
    }

    [Fact]
    public async Task TC_UC13_05_UC14_05_entire_dictation_review_manual_completion_and_signature_work_from_the_keyboard()
    {
        var (patient, session) = await AccountAsync();
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        await using var context = await ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await StartAndReviewAsync(page, $"{server.Url}/patients/{patient}/dictate");
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('[data-dictation-field=subjective] [value=accepted]')"));
        Assert.Contains("Cough", await page.Locator("mark.sc-transcript__source").First.TextContentAsync());
        // The app bar's logo is drawn here too, on the second page that shows it, where the browser only checks it again.
        Assert.True(await page.EvaluateAsync<bool>("document.querySelector('.sc-appbar__logo').naturalWidth > 0"));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync("document.querySelector('[data-review-progress]').textContent.includes('1 of 3')");
        await TabToAsync(page, "[data-dictation-field=assessment] summary");
        await page.Keyboard.PressAsync("Enter");
        await page.Keyboard.PressAsync("Tab");
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('#edited-assessment')"));
        await page.Keyboard.PressAsync("ControlOrMeta+A");
        await page.Keyboard.InsertTextAsync("Synthetic clinician-corrected assessment");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync("document.querySelector('[data-review-progress]').textContent.includes('2 of 3')");
        await TabToAsync(page, "[data-dictation-field=plan] [value=rejected]");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync("document.querySelector('[data-review-progress]').textContent.includes('3 of 3')");
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('[data-review-complete] button')"));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync(url => !url.Contains("/review/", StringComparison.Ordinal) && url.Contains("/notes/drafts/", StringComparison.Ordinal));
        // Nothing dictated was sorted into Objective here: the clinician writes it.
        Assert.Equal("Cough for three days.", await page.Locator("#Subjective").InputValueAsync());
        Assert.Equal("", await page.Locator("#Objective").InputValueAsync());
        Assert.Equal("Synthetic clinician-corrected assessment", await page.Locator("#Assessment").InputValueAsync());
        Assert.Equal("", await page.Locator("#Plan").InputValueAsync());
        await TabToAsync(page, "#Objective");
        await page.Keyboard.InsertTextAsync("Synthetic manual examination");
        await TabToAsync(page, "#Plan");
        await page.Keyboard.InsertTextAsync("Synthetic manual follow-up plan");
        await page.WaitForFunctionAsync("document.getElementById('note-save-status').textContent === 'Saved'");
        await TabToAsync(page, "[data-review]");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/confirm?version=*");
        await TabToAsync(page, "[name=Confirmed][type=checkbox]");
        await page.Keyboard.PressAsync("Space");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/notes/*?signed=True");
        Assert.Contains("Signed · read-only", await page.Locator("main").TextContentAsync());
        Assert.Contains("Synthetic manual follow-up plan", await page.Locator("main").TextContentAsync());
        Assert.Equal(0, await page.Locator("textarea").CountAsync());
        var storage = await page.EvaluateAsync<string>("JSON.stringify([Object.entries(localStorage), Object.entries(sessionStorage)])");
        Assert.DoesNotContain(patient.ToString(), storage);
        Assert.DoesNotContain("Synthetic", storage);
        Assert.Empty(errors);
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.SignNote, patientId: patient));
    }

    // TC-UC14-05 (NFR-12) in the SE document (Satwik Yash Padhy): write and sign a note with the keyboard only, typed
    // from scratch with no dictation. From the chart, the keyboard reaches "New note", fills Subjective, Objective,
    // Assessment and Plan in the editor (step 14), which saves them, then reviews, confirms and signs. The test above is TC-UC14-05 through
    // dictation review.
    [Fact]
    public async Task TC_UC14_05_a_note_typed_from_scratch_is_written_and_signed_from_the_keyboard()
    {
        var (patient, session) = await AccountAsync();
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        await using var context = await ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync($"{server.Url}/patients/{patient}");

        await TabToAsync(page, "form[action*='handler=NewNote'] button");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/notes/drafts/**");
        await TabToAsync(page, "#Subjective");
        await page.Keyboard.InsertTextAsync("Synthetic keyboard subjective");
        await TabToAsync(page, "#Objective");
        await page.Keyboard.InsertTextAsync("Synthetic keyboard objective");
        await TabToAsync(page, "#Assessment");
        await page.Keyboard.InsertTextAsync("Synthetic keyboard assessment");
        await TabToAsync(page, "#Plan");
        await page.Keyboard.InsertTextAsync("Synthetic keyboard plan");
        await page.WaitForFunctionAsync("document.getElementById('note-save-status').textContent === 'Saved'");
        await TabToAsync(page, "[data-review]");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/confirm?version=*");
        await TabToAsync(page, "[name=Confirmed][type=checkbox]");
        await page.Keyboard.PressAsync("Space");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/notes/*?signed=True");

        var signed = await page.Locator("main").TextContentAsync();
        Assert.Contains("Signed · read-only", signed);
        Assert.Contains("Synthetic keyboard subjective", signed);
        Assert.Contains("Synthetic keyboard objective", signed);
        Assert.Contains("Synthetic keyboard assessment", signed);
        Assert.Contains("Synthetic keyboard plan", signed);
        Assert.Equal(0, await page.Locator("textarea").CountAsync());
        Assert.Empty(errors);
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.SignNote, patientId: patient));
    }

    [Fact]
    public async Task Failed_review_requests_preserve_edited_text_focus_errors_and_retry_without_duplicate_decisions()
    {
        var (patient, session) = await AccountAsync();
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        await using var context = await ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        await StartAndReviewAsync(page, $"{server.Url}/patients/{patient}/dictate");
        await page.Locator("[data-dictation-field=subjective] summary").PressAsync("Enter");
        await page.Locator("#edited-subjective").FillAsync("Synthetic corrected text retained after failure");
        await page.RouteAsync("**/*handler=Decide", route => route.AbortAsync("connectionrefused"));
        await page.Locator("[data-dictation-field=subjective] [value=edited]").PressAsync("Enter");
        await page.WaitForFunctionAsync("document.getElementById('note-save-status').textContent === 'Save failed'");
        Assert.Equal("Synthetic corrected text retained after failure", await page.Locator("#edited-subjective").InputValueAsync());
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.id === 'note-errors'"));
        Assert.Contains("0 of 3", await page.Locator("[data-review-progress]").TextContentAsync());
        await page.UnrouteAsync("**/*handler=Decide");
        await page.Locator("[data-dictation-field=subjective] [value=edited]").PressAsync("Enter");
        await page.WaitForFunctionAsync("document.querySelector('[data-review-progress]').textContent.includes('1 of 3')");
        Assert.Contains("Synthetic corrected text retained after failure", await page.Locator("[data-dictation-field=subjective] [data-approved-value]").TextContentAsync());
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.AiFieldReviewed, patientId: patient));
    }

    [Fact]
    public async Task A_failed_result_handoff_keeps_the_transcript_and_retries_into_the_saved_review()
    {
        var (patient, session) = await AccountAsync();
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        await using var context = await ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        await page.RouteAsync("**/review/*/stage", route => route.AbortAsync("connectionrefused"));
        await page.GotoAsync($"{server.Url}/patients/{patient}/dictate");
        await page.Keyboard.PressAsync("Alt+Shift+D");
        await page.WaitForFunctionAsync("document.querySelector('[data-dictation-transcript]').textContent.includes('Fluids and rest.')");
        await page.Keyboard.PressAsync("Alt+Shift+D");
        await page.WaitForFunctionAsync("document.querySelector('[data-dictation-status]').textContent.includes('could not confirm')");
        Assert.EndsWith("/dictate", page.Url);
        Assert.Contains("Cough for three days.", await page.Locator("[data-dictation-transcript]").TextContentAsync());
        Assert.False(await page.Locator("[data-dictation-manual]").IsHiddenAsync());
        await page.UnrouteAsync("**/review/*/stage");
        await page.Locator("[data-dictation-review]").PressAsync("Enter");
        try
        {
            await page.WaitForURLAsync("**/review/*");
        }
        catch (TimeoutException)
        {
            var status = await page.Locator("[data-dictation-status]").TextContentAsync();
            Assert.Fail($"The retry never reached the review. Page: {page.Url}. Status: \"{status}\".");
        }

        Assert.Contains("0 of 3", await page.Locator("[data-review-progress]").TextContentAsync());
    }
}

[CollectionDefinition(Collection, DisableParallelization = true)]
public sealed class BrowserTests
{
    public const string Collection = "Browser tests";
}
