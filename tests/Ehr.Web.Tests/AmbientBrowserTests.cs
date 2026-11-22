using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Listening to a visit (the expansion plan's step 16; UC 23) in a real browser, from the keyboard (NFR-12), with a fake
// microphone and recorder and the stub's sessions that listen: no real audio or model. The page itself records, in
// window.__ambient, each time it asked for the microphone and each call it made and the answer, so the tests can say in
// which order things happened. In the browser tests' collection, for the reason AiReviewBrowserTests gives.
[Collection(BrowserTests.Collection)]
public class AmbientBrowserTests(WebApps apps) : IClassFixture<WebApps>
{
    internal const string FakeMicrophone = """
        window.__ambient = { events: [], track: { enabled: true, stopped: false, stop() { this.stopped = true; } } };
        const stream = { getTracks: () => [window.__ambient.track], getAudioTracks: () => [window.__ambient.track] };
        Object.defineProperty(navigator, 'mediaDevices', { value: { getUserMedia: async () => { window.__ambient.events.push('microphone'); return stream; } } });
        const fetched = window.fetch.bind(window);
        window.fetch = async (url, options) => {
            const call = `${options?.method ?? 'GET'} ${new URL(url, location.href).pathname}`;
            window.__ambient.events.push(`sent ${call}`);
            const response = await fetched(url, options);
            window.__ambient.events.push(`${call} ${response.status}`);
            return response;
        };
        window.MediaRecorder = class extends EventTarget {
            constructor() { super(); this.state = 'inactive'; this.mimeType = 'audio/webm'; }
            start() {
                this.state = 'recording';
                this.timer = setInterval(() => {
                    if (this.state === 'recording') this.dispatchEvent(new BlobEvent('dataavailable', { data: new Blob(['synthetic audio'], { type: this.mimeType }) }));
                }, 60);
            }
            pause() { this.state = 'paused'; }
            resume() { this.state = 'recording'; }
            stop() {
                clearInterval(this.timer);
                this.state = 'inactive';
                this.dispatchEvent(new BlobEvent('dataavailable', { data: new Blob(['synthetic audio'], { type: this.mimeType }) }));
                this.dispatchEvent(new Event('stop'));
            }
        };
        """;

    internal static async Task<(Guid Patient, Session Session)> AccountAsync(WebApps apps)
    {
        apps.Dictation.Reset();
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Synthetic", "Keyboard listening");
        await apps.AssignAsync(clinician, patient);
        return (patient, await SignInAsync(apps.OnPrem, clinician));
    }

    internal static async Task<IBrowserContext> ContextAsync(IBrowser browser, Session session)
    {
        var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        await context.AddCookiesAsync(session.Jar.GetCookies(Base).Cast<System.Net.Cookie>().Select(c => new Microsoft.Playwright.Cookie
        { Name = c.Name, Value = c.Value, Domain = "localhost", Path = c.Path, Secure = true, HttpOnly = c.HttpOnly, SameSite = SameSiteAttribute.Strict }));
        await context.AddInitScriptAsync(FakeMicrophone);
        return context;
    }

    internal static Task<string[]> EventsAsync(IPage page) => page.EvaluateAsync<string[]>("window.__ambient.events");

    // The chunks of audio the page has sent, whether or not they have been answered yet.
    internal static async Task<int> ChunksAsync(IPage page) =>
        (await EventsAsync(page)).Count(e => e.StartsWith("sent ", StringComparison.Ordinal) && e.Contains("/audio", StringComparison.Ordinal));

    // Fills in the consent from the keyboard: the radio buttons with Space, Tab between, and Start listening with Enter.
    internal static async Task ConsentAsync(IPage page, bool agreed = true)
    {
        await page.FocusAsync("#ambient-speakers-2");
        await page.Keyboard.PressAsync("Space");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Space");
        await page.Keyboard.PressAsync("Tab");
        if (agreed)
        {
            await page.Keyboard.PressAsync("Space");
        }

        await page.Keyboard.PressAsync("Tab");
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('[data-ambient-start]')"));
        await page.Keyboard.PressAsync("Enter");
    }

    // TC-UC23-02 in the browser: Stop and discard switches the microphone off at once and discards the session and its
    // review; the consent is asked for again before anything else is recorded.
    [Fact]
    public async Task TC_UC23_02_stop_and_discard_switches_the_microphone_off_and_keeps_nothing()
    {
        var (patient, session) = await AccountAsync(apps);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{server.Url}/patients/{patient}/listen");
        await ConsentAsync(page);
        await page.WaitForFunctionAsync("window.__ambient.events.some(e => e.includes('/audio'))");

        await AiReviewBrowserTests.TabToAsync(page, "[data-ambient-discard]");
        await page.Keyboard.PressAsync("Enter");
        Assert.True(await page.EvaluateAsync<bool>("window.__ambient.track.stopped && !window.__ambient.track.enabled"));
        await page.WaitForFunctionAsync("window.__ambient.events.some(e => e.startsWith('DELETE ') && e.endsWith(' 204'))"
            + " && window.__ambient.events.some(e => e.endsWith('/discard 200'))");
        Assert.Contains("Discarded: nothing recorded was kept.", await page.TextContentAsync("[data-ambient-status]"));
        Assert.True(await page.IsVisibleAsync("[data-ambient-consent]"));
        Assert.False(await page.IsCheckedAsync("#ambient-agreed"));
        Assert.DoesNotContain("Recording", await page.TitleAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(apps.Dictation.Received, r => r.Method == "POST" && r.PathAndQuery.EndsWith("/stop", StringComparison.Ordinal));
    }
}
