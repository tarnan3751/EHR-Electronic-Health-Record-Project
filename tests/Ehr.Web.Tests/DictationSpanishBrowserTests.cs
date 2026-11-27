using System.Globalization;
using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.AiReviewBrowserTests;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The Dictate page's choice of language (the expansion plan's step 17) in a real browser, recording with the browser
// tests' stand-in microphone (AiReviewBrowserTests): from the keyboard (NFR-12), in both themes, and never remembered.
// Then each language's limit, 2 minutes in Spanish and 3 in English: the time left while recording, in both themes, and
// the stop at the limit, on a stand-in microphone of this class's own. In the browser tests' collection, for the reason
// AiReviewBrowserTests gives.
[Collection(BrowserTests.Collection)]
public class DictationSpanishBrowserTests(WebApps apps) : IClassFixture<WebApps>
{
    const string Hint = "Experimental The transcript stays in Spanish; the proposed note is in English.";

    // A stand-in microphone that runs a dictation to its limit in a moment: AiReviewBrowserTests' with no end to its
    // chunks, one every 10 milliseconds, each 2 seconds of audio to the stub. Stopped, it hands over the last of the
    // audio and then stops, a moment later, as a browser's recorder does. Beside it, what the Dictate page shows as the
    // time left, each value once, and everything its status line says, in order (the page leaves for the review after).
    const string ToTheLimit = """
        Object.defineProperty(navigator, 'mediaDevices', { value: { getUserMedia: async () => ({ getTracks: () => [{ stop() {} }] }) } });
        window.MediaRecorder = class extends EventTarget {
            constructor() { super(); this.state = 'inactive'; this.mimeType = 'audio/webm'; }
            chunk() { this.dispatchEvent(new BlobEvent('dataavailable', { data: new Blob(['synthetic audio'], { type: this.mimeType }) })); }
            start() { this.state = 'recording'; this.timer = setInterval(() => this.chunk(), 10); }
            stop() {
                this.state = 'inactive';
                clearInterval(this.timer);
                setTimeout(() => { this.chunk(); this.dispatchEvent(new Event('stop')); });
            }
        };
        window.seen = { left: [], said: [] };
        addEventListener('DOMContentLoaded', () => {
            const left = document.querySelector('[data-dictation-time-left]');
            const status = document.querySelector('[data-dictation-status]');
            if (!left) return;
            new MutationObserver(() => {
                if (!left.hidden && seen.left.at(-1) !== left.textContent) seen.left.push(left.textContent);
            }).observe(left, { attributes: true, childList: true, characterData: true, subtree: true });
            new MutationObserver(() => seen.said.push(status.textContent)).observe(status, { childList: true });
        });
        """;

    async Task<(Guid Patient, Session Session)> AccountAsync()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Synthetic", "Dictado teclado");
        await apps.AssignAsync(clinician, patient);
        return (patient, await SignInAsync(apps.OnPrem, clinician));
    }

    // TC-UC9-12 (NFR-12, proposed FR-61), and TC-UC9-07 and TC-UC9-10 on the page: with the keyboard only, the clinician
    // moves to the language, picks Spanish, reads the hint, labelled experimental, dictates and stops with Alt+Shift+D; the choice is fixed
    // while recording; the live transcript is marked Spanish, its passage not heard as Spanish marked and explained; the
    // dictation reaches its review in Spanish. Reloaded, the page is back to English (TC-UC9-08).
    [Fact]
    public async Task TC_UC9_12_spanish_is_chosen_and_dictated_from_the_keyboard()
    {
        apps.Dictation.Reset();
        var (patient, session) = await AccountAsync();
        await using var opened = await BrowserPage.OpenAsync(apps, browser => ContextAsync(browser, session));
        var (server, page, errors) = opened;
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
        Assert.Contains("Translated from Spanish.", await page.ContentAsync());
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
        await using var opened = await BrowserPage.OpenAsync(apps, browser => ContextAsync(browser, session));
        var (server, page, _) = opened;
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
        await using var opened = await BrowserPage.OpenAsync(apps, browser => ContextAsync(browser, session));
        var (server, page, errors) = opened;
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

    // TC-UC9-13 (proposed FR-61, the decision log's step 17, decision 20; NFR-12): a dictation run to its limit, the
    // service's own, 2 minutes in Spanish and 3 in English, on the stand-in microphone above. While recording, the time
    // left beside Stop counts down from the limit, chunk by chunk, in no live region; between "Recording" and the limit
    // the status line says 1 minute, 30 seconds and 10 seconds left, and nothing else. At the limit the service refuses
    // the next chunk, and the page sends nothing more, the last of the audio included, stops, and shows that the
    // dictation stopped at its limit, naming it, with what happens next; the status line says the limit once, with the
    // transcript's finishing, and keeps it while the stop is held. Read there, the dictation then goes on to its review.
    [Theory]
    [InlineData("es", 2)]
    [InlineData("en", 3)]
    public async Task TC_UC9_13_the_time_left_counts_down_and_the_dictation_stops_at_its_limit(string language, int minutes)
    {
        apps.Dictation.Reset();
        var (patient, session) = await AccountAsync();
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        await context.AddCookiesAsync(session.Jar.GetCookies(Base).Cast<System.Net.Cookie>().Select(c => new Microsoft.Playwright.Cookie
        { Name = c.Name, Value = c.Value, Domain = "localhost", Path = c.Path, Secure = true, HttpOnly = c.HttpOnly, SameSite = SameSiteAttribute.Strict }));
        await context.AddInitScriptAsync(ToTheLimit);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/stop", async route =>
        {
            stopping.TrySetResult();
            await release.Task;
            await route.ContinueAsync();
        });
        await page.GotoAsync($"{server.Url}/patients/{patient}/dictate");
        if (language == "es")
        {
            await page.Locator("[data-dictation-language]").SelectOptionAsync("es");
            Assert.Contains("the proposed note is in English.", await page.Locator("[data-dictation-language-hint]").TextContentAsync());
        }

        await page.Locator("[data-dictation-start]").ClickAsync();
        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        // From the whole limit to none, 2 seconds a chunk.
        var limit = minutes * 60;
        var chunks = limit / 2;
        Assert.Equal(
            Enumerable.Range(0, chunks + 1).Select(chunk => limit - chunk * 2).Select(left => string.Create(CultureInfo.InvariantCulture, $"{left / 60}:{left % 60:00} left")),
            await page.EvaluateAsync<string[]>("seen.left"));
        var stopped = $"The dictation stopped at its {minutes}-minute limit.";
        Assert.Equal(
            [
                "Recording. Speak your note, then select Stop.",
                $"Dictation stops in a minute, at its {minutes}-minute limit.",
                "30 seconds of dictation left. Stop soon, then dictate the rest separately.",
                "10 seconds of dictation left. Stop soon, then dictate the rest separately.",
                $"{stopped} Finishing the transcript and proposing the fields. This can take up to a minute.",
            ],
            await page.EvaluateAsync<string[]>("seen.said"));
        Assert.Equal($"{stopped} Dictate the rest after reviewing this one.",
            await page.Locator("[data-dictation-limit]").TextContentAsync());
        Assert.True(await page.Locator("[data-dictation-limit]").IsVisibleAsync());
        Assert.False(await page.Locator("[data-dictation-time-left]").IsVisibleAsync());
        Assert.True(await page.Locator("[data-dictation-stop]").IsDisabledAsync());
        Assert.True(await page.EvaluateAsync<bool>("""
            document.querySelector('[data-dictation-time-left]').closest('[aria-live], [role=status], [role=alert], [role=log], [role=timer], [role=marquee]') === null
            """));
        // The chunks the limit holds, and the one past it, refused; nothing after.
        var audio = apps.Dictation.Received.Select(r => r.PathAndQuery).Where(path => path.Contains("/audio?seq=", StringComparison.Ordinal)).ToList();
        Assert.Equal(Enumerable.Range(0, chunks + 1), audio.Select(path => int.Parse(path[(path.LastIndexOf('=') + 1)..], CultureInfo.InvariantCulture)));

        release.SetResult();
        await page.WaitForURLAsync("**/review/*");
        var sent = apps.Dictation.Received.Select(r => r.PathAndQuery).ToList();
        Assert.Equal(audio.Count, sent.Count(path => path.Contains("/audio?seq=", StringComparison.Ordinal)));
        Assert.True(sent.FindIndex(path => path.EndsWith("/stop", StringComparison.Ordinal)) > sent.IndexOf(audio[^1]));
        Assert.Empty(errors);
    }

    // TC-UC9-13 (NFR-12), the themes: the time left while recording, at 1,280 and 360 pixels in the light theme and the
    // dark, follows Stop and fits the window, its words and its digits differ in color from what they sit on, and the
    // digits are in IBM Plex Mono. The browser tests' stand-in microphone hands over five chunks and carries on recording.
    [Fact]
    public async Task The_time_left_fits_every_width_in_both_themes()
    {
        apps.Dictation.Reset();
        var (patient, session) = await AccountAsync();
        await using var opened = await BrowserPage.OpenAsync(apps, browser => ContextAsync(browser, session));
        var (server, page, _) = opened;
        await page.GotoAsync($"{server.Url}/patients/{patient}/dictate");
        await page.Locator("[data-dictation-language]").SelectOptionAsync("es");
        await page.Locator("[data-dictation-start]").ClickAsync();
        await page.WaitForFunctionAsync("document.querySelector('[data-dictation-time-left]').textContent === '1:50 left'");
        const string Layout = """
            () => {
                const width = window.innerWidth;
                const left = document.querySelector('[data-dictation-time-left]');
                const digits = left.querySelector('[data-dictation-time-left-value]');
                const box = left.getBoundingClientRect();
                let back = left;
                while (back && getComputedStyle(back).backgroundColor === 'rgba(0, 0, 0, 0)') back = back.parentElement;
                return { scrolls: document.documentElement.scrollWidth > width, inside: box.width > 0 && box.left >= 0 && box.right <= width,
                    beside: left.parentElement === document.querySelector('[data-dictation-stop]').parentElement,
                    color: getComputedStyle(left).color, digits: getComputedStyle(digits).color, font: getComputedStyle(digits).fontFamily,
                    background: back ? getComputedStyle(back).backgroundColor : 'none' };
            }
            """;
        foreach (var scheme in new[] { ColorScheme.Light, ColorScheme.Dark })
        {
            foreach (var width in new[] { 1280, 360 })
            {
                await page.EmulateMediaAsync(new() { ColorScheme = scheme });
                await page.SetViewportSizeAsync(width, 800);
                var layout = await page.EvaluateAsync<System.Text.Json.JsonElement>(Layout);
                var where = $"at {width} pixels in the {scheme} theme";
                Assert.False(layout.GetProperty("scrolls").GetBoolean(), $"The page scrolls sideways {where}.");
                Assert.True(layout.GetProperty("inside").GetBoolean(), $"The time left runs past the window {where}.");
                Assert.True(layout.GetProperty("beside").GetBoolean(), $"The time left isn't beside Stop {where}.");
                Assert.NotEqual(layout.GetProperty("background").GetString(), layout.GetProperty("color").GetString());
                Assert.NotEqual(layout.GetProperty("background").GetString(), layout.GetProperty("digits").GetString());
                Assert.Contains("IBM Plex Mono", layout.GetProperty("font").GetString());
            }
        }
    }

    // TC-UC9-12 (NFR-12), the themes: the choice and its hint, at 1,280 and 640 pixels in the light theme and the dark,
    // fit the window, and the hint's words differ in color from what they sit on, as do those of its "Experimental"
    // tag (the owner's ruling of 2026-11-25), which says it in words, not only in color.
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
                const tag = hint.querySelector('.sc-tag');
                let back = hint;
                while (back && getComputedStyle(back).backgroundColor === 'rgba(0, 0, 0, 0)') back = back.parentElement;
                return { scrolls: document.documentElement.scrollWidth > width,
                    inside: choice.right <= width && hint.getBoundingClientRect().right <= width && tag.getBoundingClientRect().right <= width,
                    color: style.color, background: back ? getComputedStyle(back).backgroundColor : 'none',
                    tag: tag.textContent, tagColor: getComputedStyle(tag).color, tagBackground: getComputedStyle(tag).backgroundColor };
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
                Assert.Equal("Experimental", layout.GetProperty("tag").GetString());
                Assert.NotEqual(layout.GetProperty("tagBackground").GetString(), layout.GetProperty("tagColor").GetString());
            }
        }
    }
}
