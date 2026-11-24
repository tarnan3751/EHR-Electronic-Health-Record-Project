using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Listening to a visit (the expansion plan's step 16; UC 23) in a real browser, from the keyboard (NFR-12), with a fake
// microphone and recorder and the stub's sessions that listen: no real audio or model. The page itself records, in
// window.__ambient, each time it asked for the microphone and each call it made and the answer, so the tests can say in
// which order things happened. Each recorder's chunks say which recorder made them ("synthetic audio 2" for the second),
// and, as a real recorder's, its last chunk and its stop come after stop() returns: later still while a test holds them
// (window.__ambient.hold, then release()). In the browser tests' collection, for the reason AiReviewBrowserTests gives.
[Collection(BrowserTests.Collection)]
public class AmbientBrowserTests(WebApps apps) : IClassFixture<WebApps>
{
    internal const string FakeMicrophone = """
        window.__ambient = { events: [], recorders: 0, hold: false, held: [], release() { this.held.splice(0).forEach((fire) => fire()); } };
        Object.defineProperty(navigator, 'mediaDevices', { value: { getUserMedia: async () => {
            window.__ambient.events.push('microphone');
            const track = { enabled: true, stopped: false, stop() { this.stopped = true; } };
            window.__ambient.track = track;
            return { getTracks: () => [track], getAudioTracks: () => [track] };
        } } });
        const fetched = window.fetch.bind(window);
        window.fetch = async (url, options) => {
            const call = `${options?.method ?? 'GET'} ${new URL(url, location.href).pathname}`;
            window.__ambient.events.push(`sent ${call}`);
            const response = await fetched(url, options);
            window.__ambient.events.push(`${call} ${response.status}`);
            return response;
        };
        window.MediaRecorder = class extends EventTarget {
            constructor() { super(); this.state = 'inactive'; this.mimeType = 'audio/webm'; this.number = ++window.__ambient.recorders; }
            chunk() { return new BlobEvent('dataavailable', { data: new Blob([`synthetic audio ${this.number}`], { type: this.mimeType }) }); }
            start() {
                this.state = 'recording';
                this.timer = setInterval(() => { if (this.state === 'recording') this.dispatchEvent(this.chunk()); }, 60);
            }
            pause() { this.state = 'paused'; }
            resume() { this.state = 'recording'; }
            stop() {
                clearInterval(this.timer);
                this.state = 'inactive';
                const last = () => { this.dispatchEvent(this.chunk()); this.dispatchEvent(new Event('stop')); };
                if (window.__ambient.hold) window.__ambient.held.push(last); else setTimeout(last, 0);
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
        // The button pressed has gone with the rest: the cursor is on the consent's heading, to start again from.
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('[data-ambient-consent-heading]')"));
        Assert.False(await page.IsCheckedAsync("#ambient-agreed"));
        Assert.DoesNotContain("Recording", await page.TitleAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(apps.Dictation.Received, r => r.Method == "POST" && r.PathAndQuery.EndsWith("/stop", StringComparison.Ordinal));
    }

    // Opens the Listen page in its own browser, with the fake microphone.
    static async Task<(IPage Page, IAsyncDisposable Browser)> ListenAsync(Guid patient, Session session, ClinicalBrowserHost server, IPlaywright playwright)
    {
        var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        var context = await ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{server.Url}/patients/{patient}/listen");
        return (page, browser);
    }

    // TC-UC23-02 in the browser: a recorder's last chunk, handed over after its listening was discarded, is dropped. Held
    // here until the clinician has started listening again, it would otherwise be sent as the new listening's audio.
    [Fact]
    public async Task TC_UC23_02_a_discarded_recorders_last_chunk_never_reaches_the_next_listening()
    {
        var (patient, session) = await AccountAsync(apps);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        var (page, browser) = await ListenAsync(patient, session, server, playwright);
        await using var _ = browser;
        await ConsentAsync(page);
        await page.WaitForFunctionAsync("window.__ambient.events.some(e => e.includes('/audio') && e.endsWith(' 200'))");
        var first = apps.Dictation.Received.First(r => r.PathAndQuery.Contains("/audio", StringComparison.Ordinal)).PathAndQuery.Split('/')[3];

        await page.EvaluateAsync("window.__ambient.hold = true");
        await AiReviewBrowserTests.TabToAsync(page, "[data-ambient-discard]");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync("window.__ambient.events.some(e => e.endsWith('/discard 200'))");
        await page.EvaluateAsync("window.__ambient.hold = false");
        await ConsentAsync(page);
        await page.WaitForFunctionAsync("window.__ambient.events.filter(e => e === 'microphone').length === 2");
        await page.WaitForFunctionAsync(
            "window.__ambient.events.slice(window.__ambient.events.lastIndexOf('microphone')).some(e => e.includes('/audio') && e.endsWith(' 200'))");
        await page.EvaluateAsync("window.__ambient.release()");
        await page.WaitForTimeoutAsync(500);

        var next = apps.Dictation.Received.Where(r => r.PathAndQuery.Contains("/audio", StringComparison.Ordinal) && !r.PathAndQuery.Contains(first, StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(next);
        Assert.All(next, chunk => Assert.Equal("synthetic audio 2", System.Text.Encoding.UTF8.GetString(chunk.Body)));
    }

    // TC-UC23-02 in the browser: leaving the page ends listening for good, the microphone off and nothing sent after,
    // and coming back to it from the browser's back/forward cache shows that it ended, never "Recording".
    [Fact]
    public async Task TC_UC23_02_leaving_the_page_ends_listening_for_good()
    {
        var (patient, session) = await AccountAsync(apps);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        var (page, browser) = await ListenAsync(patient, session, server, playwright);
        await using var _ = browser;
        await ConsentAsync(page);
        await page.WaitForFunctionAsync("window.__ambient.events.some(e => e.includes('/audio'))");

        await page.EvaluateAsync("window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: true }))");
        Assert.True(await page.EvaluateAsync<bool>("window.__ambient.track.stopped && !window.__ambient.track.enabled"));
        await page.WaitForFunctionAsync("window.__ambient.events.some(e => e.startsWith('DELETE ') && e.endsWith(' 204'))"
            + " && window.__ambient.events.some(e => e.endsWith('/discard 200'))");
        var sent = await ChunksAsync(page);
        await page.WaitForTimeoutAsync(400);
        Assert.Equal(sent, await ChunksAsync(page));

        await page.EvaluateAsync("window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true }))");
        Assert.Contains("Listening ended when you left this page: nothing recorded was kept.", await page.TextContentAsync("[data-ambient-status]"));
        Assert.DoesNotContain("Recording", await page.TitleAsync(), StringComparison.Ordinal);
        Assert.True(await page.IsHiddenAsync("[data-ambient-capture]"));
        Assert.True(await page.IsVisibleAsync("[data-ambient-consent]"));
    }

    // TC-UC23-06 in the browser: the page goes out of sight while the start is on its way. Listening is paused the moment
    // the recorder starts: the microphone's track off, nothing sent, and the status line says why.
    [Fact]
    public async Task TC_UC23_06_out_of_sight_while_listening_starts_it_is_paused_at_once()
    {
        var (patient, session) = await AccountAsync(apps);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        var (page, browser) = await ListenAsync(patient, session, server, playwright);
        await using var _ = browser;
        await page.EvaluateAsync("""
            const send = window.fetch;
            window.fetch = async (url, options) => {
                const response = await send(url, options);
                if (new URL(url, location.href).pathname.endsWith('/ambient/start')) {
                    Object.defineProperty(document, 'visibilityState', { value: 'hidden', configurable: true });
                    document.dispatchEvent(new Event('visibilitychange'));
                }
                return response;
            };
            """);
        await ConsentAsync(page);
        await page.WaitForFunctionAsync("window.__ambient.events.includes('microphone') && window.__ambient.recorders === 1");
        await page.WaitForTimeoutAsync(400);
        Assert.False(await page.EvaluateAsync<bool>("window.__ambient.track.enabled"));
        Assert.Equal(0, await ChunksAsync(page));
        Assert.StartsWith("Paused while this page was out of sight", await page.TextContentAsync("[data-ambient-status]"));
        Assert.StartsWith("Paused", await page.TitleAsync(), StringComparison.Ordinal);
    }

    // TC-UC23-06 in the browser: the clock ticks beside the status line, not in it, so a screen reader isn't read the time
    // every second; the status line says what changes, and a warning stays there until the next message.
    [Fact]
    public async Task TC_UC23_06_the_clock_isnt_announced_and_a_warning_stays_until_the_next_message()
    {
        var (patient, session) = await AccountAsync(apps);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        var (page, browser) = await ListenAsync(patient, session, server, playwright);
        await using var _ = browser;
        await ConsentAsync(page);
        await page.WaitForFunctionAsync("document.querySelector('[data-ambient-clock]').textContent.startsWith('Recording the visit')");
        Assert.Equal("Listening: the visit is being recorded.", await page.TextContentAsync("[data-ambient-status]"));
        await page.EvaluateAsync("""
            window.__said = [];
            const status = document.querySelector('[data-ambient-status]');
            new MutationObserver(() => window.__said.push(status.textContent)).observe(status, { childList: true, characterData: true, subtree: true });
            """);
        await page.WaitForTimeoutAsync(2500);
        Assert.Empty(await page.EvaluateAsync<string[]>("window.__said"));
        Assert.Matches(@"^Recording the visit: 0:0[2-9] of 20:00\.$", await page.TextContentAsync("[data-ambient-clock]"));
        Assert.Equal("recording", await page.GetAttributeAsync("[data-ambient-clock]", "data-state"));

        apps.Dictation.QuietSeconds = 250;
        await page.WaitForFunctionAsync("document.querySelector('[data-ambient-status]').textContent.startsWith('Nobody has spoken for 4 minutes')");
        await page.WaitForTimeoutAsync(2000);
        Assert.StartsWith("Nobody has spoken for 4 minutes", await page.TextContentAsync("[data-ambient-status]"));
    }

    // TC-UC23-06 in the browser: three voices told apart under a consent for the patient and the clinician. The third can
    // only be "I can't tell", as the page says, and from the keyboard: typing a choice's first letter, Tab between. Its
    // words are shown as "Unsure" in the review, and the order it said is listed as said where who spoke couldn't be told.
    [Fact]
    public async Task TC_UC23_06_a_voice_the_clinician_cant_tell_is_named_from_the_keyboard_and_shown_as_unsure()
    {
        var (patient, session) = await AccountAsync(apps);
        apps.Dictation.ExtraVoice = true;
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        var (page, browser) = await ListenAsync(patient, session, server, playwright);
        await using var _ = browser;
        await ConsentAsync(page);
        await page.WaitForFunctionAsync("window.__ambient.events.some(e => e.includes('/audio') && e.endsWith(' 200'))");
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('[data-ambient-stop]')"));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForSelectorAsync("[data-ambient-voice='3']");
        Assert.True(await page.IsVisibleAsync("[data-ambient-voices-two]"));
        foreach (var voice in new[] { "1", "2", "3" })
        {
            Assert.Equal(["", "clinician", "patient", "unsure"],
                await page.EvalOnSelectorAllAsync<string[]>($"[data-ambient-voice='{voice}'] option", "options => options.map(o => o.value)"));
        }

        await page.Keyboard.TypeAsync("M");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync("T");
        Assert.True(await page.IsDisabledAsync("[data-ambient-draft]"));
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync("I");
        Assert.True(await page.IsEnabledAsync("[data-ambient-draft]"));
        await AiReviewBrowserTests.TabToAsync(page, "[data-ambient-draft]");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/review/*");
        await page.WaitForSelectorAsync(".sc-transcript__speaker");
        Assert.Contains("Unsure", await page.Locator(".sc-transcript__speaker").AllTextContentsAsync());
        // The order that voice said, listed as not carried over, with why it is no one's.
        Assert.Equal($"Order: “{DictationSidecarStub.FragmentSaid}” Said in a passage whose speaker couldn't be told.",
            await page.Locator("[data-dictation-not-carried-list] li").TextContentAsync());
    }

    // TC-UC23-04 in the browser: fetching the visit after Stop, before its voices are named. An answer this app couldn't
    // use (502) isn't asked for again; a service that doesn't answer (503) is asked a few times, then the page gives up.
    // Either way it stays alive: it says what happened and offers Try again, the cursor on it, and Discard the recording,
    // never to open the draft or retrieve it there, which can't name the voices. Trying again once the service answers
    // goes on to the voices.
    [Theory]
    [InlineData(502, """{"error":"bad_answer","message":"Dictation isn't working right now."}""", 1, "Dictation isn't working right now.")]
    [InlineData(503, """{"error":"unavailable","message":"Dictation isn't available right now."}""", 4, "The visit couldn't be fetched: the dictation service isn't answering")]
    public async Task TC_UC23_04_fetching_the_draft_is_asked_a_few_times_at_most(int status, string answer, int asked, string said)
    {
        var (patient, session) = await AccountAsync(apps);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        var (page, browser) = await ListenAsync(patient, session, server, playwright);
        await using var _ = browser;
        await page.RouteAsync("**/ambient/*/stop", route => route.FulfillAsync(new() { Status = status, ContentType = "application/json", Body = answer }));
        await ConsentAsync(page);
        await page.WaitForFunctionAsync("window.__ambient.events.some(e => e.includes('/audio') && e.endsWith(' 200'))");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync($"document.querySelector('[data-ambient-status]').textContent.startsWith({System.Text.Json.JsonSerializer.Serialize(said)})",
            null, new() { Timeout = 30_000 });
        await page.WaitForTimeoutAsync(1500);
        Assert.Equal(asked, (await EventsAsync(page)).Count(e => e.StartsWith("sent POST ", StringComparison.Ordinal) && e.EndsWith("/stop", StringComparison.Ordinal)));
        var words = await page.TextContentAsync("[data-ambient-status]");
        Assert.DoesNotContain("retrieve", words, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("draft", words, StringComparison.OrdinalIgnoreCase);
        if (status == 503)
        {
            Assert.Contains("Try again in a moment, or discard the recording; the service keeps nothing for more than 10 minutes.", words);
        }

        Assert.True(await page.IsVisibleAsync("[data-ambient-retry]"));
        Assert.True(await page.IsVisibleAsync("[data-ambient-discard-stuck]"));
        Assert.True(await page.IsHiddenAsync("[data-ambient-open-draft]"));
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('[data-ambient-retry]')"));

        await page.UnrouteAsync("**/ambient/*/stop");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForSelectorAsync("[data-ambient-voice='2']");
        Assert.True(await page.IsHiddenAsync("[data-ambient-again]"));
    }

    // TC-UC23-04 in the browser: a visit that can't be drafted, as the service answers Stop: transcribed but its voices
    // not told apart (its fields unsorted), or more voices than can be named. The page says so and offers only to discard
    // the recording, which discards the review and the service's session as Stop and discard does.
    [Theory]
    [InlineData("""{"transcript":{"text":"","final":true,"segments":[]},"sorting":"unavailable","reason":"transcription_failed","fields":null,"notCarriedOver":[],"provenance":{"speech":{"repository":"synthetic","commit":"0","sha256":"0"},"language":null,"llamaServer":null,"prompt":"p","schema":"s","structuring":"t","code":"c"}}""",
        "This visit couldn't be transcribed and its voices told apart, so it can't be drafted.")]
    [InlineData("""{"transcript":{"text":"","final":true,"segments":[]},"sorting":"awaiting_speakers","fields":null,"notCarriedOver":[],"provenance":{"speech":{"repository":"synthetic","commit":"0","sha256":"0"},"language":null,"llamaServer":null,"prompt":"p","schema":"s","structuring":"t","code":"c"},"speakers":[{"group":1,"seconds":1},{"group":2,"seconds":1},{"group":3,"seconds":1},{"group":4,"seconds":1},{"group":5,"seconds":1},{"group":6,"seconds":1},{"group":7,"seconds":1},{"group":8,"seconds":1},{"group":9,"seconds":1},{"group":10,"seconds":1}]}""",
        "The dictation service told apart more voices than can be named (9 at most), so this visit can't be drafted.")]
    public async Task TC_UC23_04_a_visit_that_cant_be_drafted_offers_only_discarding_it(string answer, string said)
    {
        var (patient, session) = await AccountAsync(apps);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        var (page, browser) = await ListenAsync(patient, session, server, playwright);
        await using var _ = browser;
        await page.RouteAsync("**/ambient/*/stop", route => route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = answer }));
        await ConsentAsync(page);
        await page.WaitForFunctionAsync("window.__ambient.events.some(e => e.includes('/audio') && e.endsWith(' 200'))");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync($"document.querySelector('[data-ambient-status]').textContent.startsWith({System.Text.Json.JsonSerializer.Serialize(said)})");
        Assert.True(await page.IsHiddenAsync("[data-ambient-retry]"));
        Assert.True(await page.IsHiddenAsync("[data-ambient-voices]") || await page.Locator("[data-ambient-voice]").CountAsync() == 0);
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('[data-ambient-discard-stuck]')"));
        Assert.DoesNotContain(await EventsAsync(page), e => e.Contains("/stage", StringComparison.Ordinal));

        await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync("window.__ambient.events.some(e => e.startsWith('DELETE ') && e.endsWith(' 204'))"
            + " && window.__ambient.events.some(e => e.endsWith('/discard 200'))");
        Assert.True(await page.IsVisibleAsync("[data-ambient-consent]"));
        Assert.True(await page.IsHiddenAsync("[data-ambient-again]"));
    }

    // TC-UC23-04 in the browser: once the voices are handed over, the draft is the service's to write and can be retrieved
    // from the draft. When it can't be fetched, the page says so, offers Try again and Open the draft, not discarding;
    // and leaving the page then keeps the visit: nothing discarded at the service or in the database. From the draft, its
    // review says it hasn't been drafted yet, with Retrieve first and Discard after it, and Retrieve then drafts it.
    [Fact]
    public async Task TC_UC23_04_after_the_voices_are_handed_over_leaving_keeps_the_visit()
    {
        var (patient, session) = await AccountAsync(apps);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        var (page, browser) = await ListenAsync(patient, session, server, playwright);
        await using var _ = browser;
        await page.RouteAsync("**/ambient/*/result", route => route.FulfillAsync(new()
        {
            Status = 503,
            ContentType = "application/json",
            Body = """{"error":"unavailable","message":"Dictation isn't available right now."}"""
        }));
        await ConsentAsync(page);
        await page.WaitForFunctionAsync("window.__ambient.events.some(e => e.includes('/audio') && e.endsWith(' 200'))");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForSelectorAsync("[data-ambient-voice='2']");
        await page.Keyboard.TypeAsync("M");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync("T");
        await AiReviewBrowserTests.TabToAsync(page, "[data-ambient-draft]");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync("document.querySelector('[data-ambient-status]').textContent.startsWith('The draft couldn\\'t be fetched')",
            null, new() { Timeout = 30_000 });
        Assert.Contains("open the draft in a few minutes to retrieve it", await page.TextContentAsync("[data-ambient-status]"));
        Assert.True(await page.IsVisibleAsync("[data-ambient-retry]"));
        Assert.True(await page.IsVisibleAsync("[data-ambient-open-draft]"));
        Assert.True(await page.IsHiddenAsync("[data-ambient-discard-stuck]"));
        var draft = (await AmbientFlows.RowsAsync(apps,
            "SELECT draft_id::text FROM ehr.ai_note_reviews WHERE patient_id = @id AND mode = 'ambient'", patient)).Single();
        Assert.Equal(await page.GetAttributeAsync("[data-ambient-open-draft]", "href"), $"/patients/{patient}/notes/drafts/{draft}");

        await page.EvaluateAsync("window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: false }))");
        await page.WaitForTimeoutAsync(500);
        Assert.DoesNotContain(await EventsAsync(page), e => e.StartsWith("sent DELETE ", StringComparison.Ordinal) || e.Contains("/discard", StringComparison.Ordinal));
        const string State = "SELECT r.state FROM ehr.ai_note_reviews r WHERE r.patient_id = @id AND r.mode = 'ambient'";
        Assert.Equal(["recording"], await AmbientFlows.RowsAsync(apps, State, patient));

        var review = (await AmbientFlows.RowsAsync(apps,
            "SELECT id::text FROM ehr.ai_note_reviews WHERE patient_id = @id AND mode = 'ambient'", patient)).Single();
        await page.UnrouteAsync("**/ambient/*/result");
        await page.GotoAsync($"{server.Url}/patients/{patient}/notes/drafts/{draft}/review/{review}");
        Assert.Contains("hasn't been drafted yet. If its voices were named a moment ago, wait a minute and choose Retrieve.",
            await page.TextContentAsync("[data-ambient-not-drafted]"));
        await AiReviewBrowserTests.TabToAsync(page, "[data-review-retrieve]");
        await page.Keyboard.PressAsync("Tab");
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('form[action*=\"handler=Discard\"] button')"));
        await page.Keyboard.PressAsync("Shift+Tab");
        await page.Keyboard.PressAsync("Enter");
        for (var i = 0; i < 60 && (await AmbientFlows.RowsAsync(apps, State, patient)).Single() != "pending"; i++)
        {
            await page.WaitForTimeoutAsync(250);
        }

        Assert.Equal(["pending"], await AmbientFlows.RowsAsync(apps, State, patient));
    }

    // TC-UC23-01 in the browser (looking at the pages, step 16): a visit's transcript is a paragraph a turn, and one
    // voice's passages keep the space between them. The shapes the transcript comes in: the dictation service's
    // (transcript.py's _join puts a space between one voice's passages, conversation.py a line break where the voice
    // changes, and a passage the voice was unsure of is a turn of its own), the stub's (a line break between every
    // passage), and a dictation's (no speakers: one paragraph, as it always was).
    [Theory]
    [InlineData("service", "Your chest is clear. Likely a viral cough.\nI have had a cough. It is worse at night. Get a chest X-ray.",
        "0:20:clinician 21:42:clinician 43:62:patient 63:84:patient 85:103:unsure",
        "Clinician Your chest is clear. Likely a viral cough.|Patient I have had a cough. It is worse at night.|Unsure Get a chest X-ray.")]
    [InlineData("stub", "Your chest is clear.\nLikely a viral cough.\nI have had a cough.",
        "0:20:clinician 21:42:clinician 43:62:patient",
        "Clinician Your chest is clear. Likely a viral cough.|Patient I have had a cough.")]
    [InlineData("dictation", "Cough for three days. Worse at night.", "0:21: 22:37:", "Cough for three days. Worse at night.")]
    public async Task TC_UC23_01_one_voices_passages_keep_the_space_between_them(string shape, string text, string segments, string shown)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        var page = await browser.NewPageAsync();
        await page.SetContentAsync("""
            <section data-view><div data-dictation-transcript></div><p data-dictation-unclear hidden></p></section>
            """);
        await page.AddScriptTagAsync(new() { Content = await File.ReadAllTextAsync(Ehr.Testing.RepositoryRoot.Combine("src/Ehr.Design/wwwroot/js/dictation-view.js"), TestContext.Current.CancellationToken) });
        var transcript = new
        {
            text,
            segments = segments.Split(' ').Select(s => s.Split(':')).Select(s => new
            {
                start = int.Parse(s[0], System.Globalization.CultureInfo.InvariantCulture),
                end = int.Parse(s[1], System.Globalization.CultureInfo.InvariantCulture),
                final = true,
                speaker = s[2] == "" ? null : s[2],
            }),
        };

        var paragraphs = await page.EvaluateAsync<string[]>("""
            transcript => {
                new window.SwiftChartDictationView(document.querySelector('[data-view]')).renderTranscript(transcript);
                return [...document.querySelectorAll('[data-dictation-transcript] p')].map((p) => p.textContent);
            }
            """, transcript);

        Assert.True(shown.Split('|').SequenceEqual(paragraphs), $"The {shape}'s transcript shows: {string.Join("|", paragraphs)}");
    }

    // TC-UC23-06 (NFR-12) in the browser: a visit's review fits a phone's width and a desktop's, in the light theme and the
    // dark, nothing wider than the window: its longest button, "Reject every proposal and delete the conversation", wraps.
    [Fact]
    public async Task TC_UC23_06_a_visits_review_fits_every_width_in_both_themes()
    {
        apps.Dictation.Reset();
        var d = await AmbientFlows.DraftAsync(apps);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        const string Layout = """
            () => {
                const width = window.innerWidth;
                const wide = [...document.querySelectorAll('body *')].filter(e => e.getBoundingClientRect().right > width + 1).map(e => e.tagName + '.' + e.className);
                const button = document.querySelector('[data-ambient-reject-all] button').getBoundingClientRect();
                return { scrolls: document.documentElement.scrollWidth > width, wide, inside: button.right <= width };
            }
            """;
        foreach (var scheme in new[] { ColorScheme.Light, ColorScheme.Dark })
        {
            foreach (var width in new[] { 1280, 360 })
            {
                await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ColorScheme = scheme, ViewportSize = new() { Width = width, Height = 800 } });
                await context.AddCookiesAsync(d.Listening.Session.Jar.GetCookies(Base).Cast<System.Net.Cookie>().Select(c => new Microsoft.Playwright.Cookie
                { Name = c.Name, Value = c.Value, Domain = "localhost", Path = c.Path, Secure = true, HttpOnly = c.HttpOnly, SameSite = SameSiteAttribute.Strict }));
                var page = await context.NewPageAsync();
                await page.GotoAsync($"{server.Url}{d.ReviewUrl}");
                var layout = await page.EvaluateAsync<System.Text.Json.JsonElement>(Layout);
                var where = $"at {width} pixels in the {scheme} theme";
                Assert.False(layout.GetProperty("scrolls").GetBoolean(), $"The review scrolls sideways {where}.");
                Assert.True(layout.GetProperty("wide").GetArrayLength() == 0, $"Wider than the window {where}: {layout.GetProperty("wide")}");
                Assert.True(layout.GetProperty("inside").GetBoolean(), $"Reject every proposal runs past the window {where}.");
            }
        }
    }
}
