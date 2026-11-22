using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.AiReviewBrowserTests;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The Dictate page's choice of language (the expansion plan's step 17) in a real browser, recording with the browser
// tests' stand-in microphone (AiReviewBrowserTests): from the keyboard (NFR-12), in both themes, never remembered, and
// stopping at the Spanish limit. In the browser tests' collection, for the reason AiReviewBrowserTests gives.
[Collection(BrowserTests.Collection)]
public class DictationSpanishBrowserTests(WebApps apps) : IClassFixture<WebApps>
{
    const string Hint = "Spanish: speak Spanish. The transcript stays in Spanish and the proposed note is in English for you to check. "
        + "You don't need to say punctuation. Up to 2 minutes in Spanish.";

    async Task<(Guid Patient, Session Session)> AccountAsync()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Synthetic", "Dictado teclado");
        await apps.AssignAsync(clinician, patient);
        return (patient, await SignInAsync(apps.OnPrem, clinician));
    }

    // TC-UC9-12 (NFR-12, proposed FR-61), and TC-UC9-07 and TC-UC9-10 on the page: with the keyboard only, the clinician
    // moves to the language, picks Spanish, reads the hint, dictates and stops with Alt+Shift+D; the choice is fixed
    // while recording; the live transcript is marked Spanish, its passage not heard as Spanish marked and explained; the
    // dictation reaches its review in Spanish. Reloaded, the page is back to English (TC-UC9-08).
    [Fact]
    public async Task TC_UC9_12_spanish_is_chosen_and_dictated_from_the_keyboard()
    {
        apps.Dictation.Reset();
        var (patient, session) = await AccountAsync();
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        await using var context = await ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        var dictate = $"{server.Url}/patients/{patient}/dictate";
        await page.GotoAsync(dictate);
        var choice = page.Locator("[data-dictation-language]");
        var hint = page.Locator("[data-dictation-language-hint]");
        Assert.False(await hint.IsVisibleAsync());

        await TabToAsync(page, "[data-dictation-language]");
        await page.Keyboard.PressAsync("s");
        await page.WaitForFunctionAsync("!document.querySelector('[data-dictation-language-hint]').hidden");
        Assert.Equal("es", await choice.InputValueAsync());
        Assert.Equal(Hint, await hint.TextContentAsync());
        Assert.Equal("dictation-language-hint", await choice.GetAttributeAsync("aria-describedby"));
        // The next stop is Dictate.
        await page.Keyboard.PressAsync("Tab");
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('[data-dictation-start]')"));

        await page.Keyboard.PressAsync("Alt+Shift+D");
        await page.WaitForFunctionAsync("document.querySelector('[data-dictation-language]').disabled");
        await page.WaitForFunctionAsync("words => document.querySelector('[data-dictation-transcript]').textContent.includes(words)",
            DictationSidecarStub.SpanishSentences[3][..12]);
        Assert.Equal("es", await page.Locator("[data-dictation-transcript]").GetAttributeAsync("lang"));
        Assert.Contains("wasn't heard as Spanish",
            await page.Locator("[data-dictation-transcript] span[lang=en][title]").First.GetAttributeAsync("title"));
        await page.Keyboard.PressAsync("Alt+Shift+D");
        await page.WaitForURLAsync("**/review/*");
        Assert.Contains("Translated from Spanish:", await page.ContentAsync());
        var start = Assert.Single(apps.Dictation.Received, r => r.PathAndQuery == "/v1/sessions");
        Assert.Equal("""{"spokenLanguage":"es"}""", System.Text.Encoding.UTF8.GetString(start.Body));

        await page.GotoAsync(dictate);
        Assert.Equal("en", await page.Locator("[data-dictation-language]").InputValueAsync());
        Assert.False(await page.Locator("[data-dictation-language-hint]").IsVisibleAsync());
        Assert.Empty(errors);
    }

    // TC-UC9-08 (proposed FR-61): a choice of Spanish the clinician leaves is not there when they come back, whether the
    // page is loaded again or the browser goes back to it.
    [Fact]
    public async Task TC_UC9_08_a_choice_of_spanish_is_never_remembered()
    {
        apps.Dictation.Reset();
        var (patient, session) = await AccountAsync();
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        await using var context = await ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        var dictate = $"{server.Url}/patients/{patient}/dictate";
        await page.GotoAsync(dictate);
        await page.Locator("[data-dictation-language]").SelectOptionAsync("es");

        await page.ReloadAsync();
        Assert.Equal("en", await page.Locator("[data-dictation-language]").InputValueAsync());

        await page.Locator("[data-dictation-language]").SelectOptionAsync("es");
        await page.GotoAsync($"{server.Url}/patients/{patient}");
        await page.GoBackAsync();
        await page.WaitForFunctionAsync("document.querySelector('[data-dictation-language]') !== null");
        Assert.Equal("en", await page.Locator("[data-dictation-language]").InputValueAsync());
        Assert.False(await page.Locator("[data-dictation-language-hint]").IsVisibleAsync());
    }

    // TC-UC9-09 (proposed FR-61), on the page: Spanish chosen on a server without Whisper, the page says so in words, the
    // microphone is let go, and the clinician can choose English and dictate.
    [Fact]
    public async Task TC_UC9_09_the_page_says_spanish_isnt_installed()
    {
        apps.Dictation.Reset().SpanishUnavailable = true;
        var (patient, session) = await AccountAsync();
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        await using var context = await ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync($"{server.Url}/patients/{patient}/dictate");
        await page.Locator("[data-dictation-language]").SelectOptionAsync("es");

        await page.Locator("[data-dictation-start]").ClickAsync();

        await page.WaitForFunctionAsync("document.querySelector('[data-dictation-status]').textContent === \"Spanish dictation isn't installed on this server.\"");
        Assert.False(await page.Locator("[data-dictation-language]").IsDisabledAsync());
        Assert.False(await page.Locator("[data-dictation-start]").IsDisabledAsync());
        await page.Locator("[data-dictation-language]").SelectOptionAsync("en");
        await page.Locator("[data-dictation-start]").ClickAsync();
        await page.WaitForFunctionAsync("document.querySelector('[data-dictation-transcript]').textContent.includes('Fluids and rest.')");
        await page.Locator("[data-dictation-stop]").ClickAsync();
        await page.WaitForURLAsync("**/review/*");
        Assert.Empty(errors);
    }

    // Decision 20 (proposed FR-61): the page says the Spanish limit, and stops recording at it as it stops an English
    // dictation at 3 minutes: the service refuses the chunk past it (the stub's limit set to 6 seconds, three of its
    // chunks), the page sends nothing more, stops, and the dictation goes on to its review with what was heard.
    [Fact]
    public async Task A_dictation_in_spanish_stops_at_its_limit()
    {
        apps.Dictation.Reset().SpanishMaxSeconds = 6;
        var (patient, session) = await AccountAsync();
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        await using var context = await ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync($"{server.Url}/patients/{patient}/dictate");
        await page.Locator("[data-dictation-language]").SelectOptionAsync("es");
        Assert.Contains("Up to 2 minutes in Spanish.", await page.Locator("[data-dictation-language-hint]").TextContentAsync());

        await page.Locator("[data-dictation-start]").ClickAsync();
        await page.WaitForURLAsync("**/review/*");

        var sent = apps.Dictation.Received.Select(r => r.PathAndQuery).ToList();
        var audio = sent.Where(path => path.Contains("/audio?seq=", StringComparison.Ordinal)).ToList();
        Assert.Equal(["0", "1", "2", "3"], audio.Select(path => path[(path.LastIndexOf('=') + 1)..]));
        Assert.True(sent.FindIndex(path => path.EndsWith("/stop", StringComparison.Ordinal)) > sent.IndexOf(audio[^1]));
        Assert.Empty(errors);
    }

    // TC-UC9-12 (NFR-12), the themes: the choice and its hint, at 1,280 and 640 pixels in the light theme and the dark,
    // fit the window, and the hint's words differ in color from what they sit on.
    [Fact]
    public async Task The_language_choice_fits_every_width_in_both_themes()
    {
        apps.Dictation.Reset();
        var (patient, session) = await AccountAsync();
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        const string Layout = """
            () => {
                const width = window.innerWidth;
                const hint = document.querySelector('[data-dictation-language-hint]');
                const choice = document.querySelector('[data-dictation-language]').getBoundingClientRect();
                const style = getComputedStyle(hint);
                let back = hint;
                while (back && getComputedStyle(back).backgroundColor === 'rgba(0, 0, 0, 0)') back = back.parentElement;
                return { scrolls: document.documentElement.scrollWidth > width, inside: choice.right <= width && hint.getBoundingClientRect().right <= width,
                    color: style.color, background: back ? getComputedStyle(back).backgroundColor : 'none' };
            }
            """;
        foreach (var scheme in new[] { ColorScheme.Light, ColorScheme.Dark })
        {
            foreach (var width in new[] { 1280, 640 })
            {
                await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ColorScheme = scheme, ViewportSize = new() { Width = width, Height = 800 } });
                await context.AddCookiesAsync(session.Jar.GetCookies(Base).Cast<System.Net.Cookie>().Select(c => new Microsoft.Playwright.Cookie
                { Name = c.Name, Value = c.Value, Domain = "localhost", Path = c.Path, Secure = true, HttpOnly = c.HttpOnly, SameSite = SameSiteAttribute.Strict }));
                var page = await context.NewPageAsync();
                await page.GotoAsync($"{server.Url}/patients/{patient}/dictate");
                await page.Locator("[data-dictation-language]").SelectOptionAsync("es");
                var layout = await page.EvaluateAsync<System.Text.Json.JsonElement>(Layout);
                var where = $"at {width} pixels in the {scheme} theme";
                Assert.False(layout.GetProperty("scrolls").GetBoolean(), $"The page scrolls sideways {where}.");
                Assert.True(layout.GetProperty("inside").GetBoolean(), $"The choice or its hint runs past the window {where}.");
                Assert.NotEqual(layout.GetProperty("background").GetString(), layout.GetProperty("color").GetString());
            }
        }
    }
}
