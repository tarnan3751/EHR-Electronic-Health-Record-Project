using Microsoft.Playwright;
using Xunit;

namespace Ehr.Web.Tests.TestCases.UC23;

// Test Case ID: TC-UC23-06
// Purpose / scenario: Listening to a visit from the keyboard alone, and nothing about the microphone before the consent:
//   the page asks for it only once the server has recorded the consent, and sends no audio before then; listening is
//   shown the whole time, pauses from the keyboard and when the page is out of sight with nothing sent meanwhile, and
//   after Stop every voice is named from the keyboard, with none chosen for the clinician, before the note is drafted
//   into its review.
// Type: Non-functional (keyboard-first, privacy)
// Requirements: NFR-12, NFR-07, proposed FR-62
// Use case: UC 23, Draft a note from a recorded visit
// Preconditions: The clinician has the patient on their panel; the on-site app in a real Chromium, with a fake
//   microphone and recorder that log each time the microphone is asked for, and the stub dictation service.
// Inputs and system conditions: The Listen page and the review page, from the keyboard: Space, Tab, Shift+Tab, Enter
//   and typing a select's first letter.
// Test data: Two speakers, agreed by the patient; the stub's conversation.
// Steps:
//   1. Fill in the consent without ticking the agreement, and press Enter on Start listening.
//   2. Tick it and press Enter on Start listening.
//   3. Pause and resume; hide the page and show it again; resume.
//   4. Stop, name voice 1 "Me" and voice 2 "The patient", and draft the note.
// Expected result: Step 1: nothing asked of the server or the microphone. Step 2: the start answered 201 before the
//   microphone is asked for, and the microphone before any audio is sent; "Recording the visit" with its dot, "●
//   Recording" in the tab's title, and the cursor on Stop and draft the note. Step 3: while paused or out of sight the
//   microphone's track is off and no chunk is sent; out of sight, it stays paused until resumed. Step 4: both voices
//   with no role, Draft the note disabled until each has one; the mapping sent as named; the review with the transcript
//   a turn at a time, "Clinician" and "Patient", and the patient's own words said to be so.
// Creator: Tahseen Arnan
[Collection(BrowserTests.Collection)]
public class TC_UC23_06_Nfr12_KeyboardConsentFirst(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Consent_first_then_listening_paused_and_stopped_and_every_voice_named_from_the_keyboard()
    {
        var (patient, session) = await AmbientBrowserTests.AccountAsync(apps);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await AmbientBrowserTests.ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync($"{server.Url}/patients/{patient}/listen");

        // Without the agreement ticked, the form asks for it, and nothing is asked of the server or the microphone: nothing
        // about the visit, that is. Every signed-in page asks its session page once as it loads (step 18), which says
        // only how long the session has left.
        await AmbientBrowserTests.ConsentAsync(page, agreed: false);
        await page.WaitForTimeoutAsync(200);
        Assert.DoesNotContain(await AmbientBrowserTests.EventsAsync(page), e => !e.Contains("GET /session", StringComparison.Ordinal));

        await page.FocusAsync("#ambient-agreed");
        await page.Keyboard.PressAsync("Space");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync("document.querySelector('[data-ambient-clock]').textContent.startsWith('Recording the visit')");
        await page.WaitForFunctionAsync("window.__ambient.events.filter(e => e.startsWith('sent ') && e.includes('/audio')).length >= 2");
        var events = await AmbientBrowserTests.EventsAsync(page);
        var started = Array.FindIndex(events, e => e.EndsWith("/ambient/start 201", StringComparison.Ordinal));
        var microphone = Array.IndexOf(events, "microphone");
        var firstAudio = Array.FindIndex(events, e => e.StartsWith("sent ", StringComparison.Ordinal) && e.Contains("/audio", StringComparison.Ordinal));
        Assert.True(started >= 0 && started < microphone && microphone < firstAudio, string.Join(" | ", events));
        Assert.StartsWith("● Recording", await page.TitleAsync(), StringComparison.Ordinal);
        Assert.Equal("recording", await page.GetAttributeAsync("[data-ambient-clock]", "data-state"));
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('[data-ambient-stop]')"));

        // Paused from the keyboard: the microphone's track is off, and no chunk goes up until it resumes.
        await page.Keyboard.PressAsync("Shift+Tab");
        await page.Keyboard.PressAsync("Enter");
        Assert.False(await page.EvaluateAsync<bool>("window.__ambient.track.enabled"));
        var paused = await AmbientBrowserTests.ChunksAsync(page);
        await page.WaitForTimeoutAsync(400);
        Assert.Equal(paused, await AmbientBrowserTests.ChunksAsync(page));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync($"window.__ambient.events.filter(e => e.startsWith('sent ') && e.includes('/audio')).length > {paused}");
        Assert.True(await page.EvaluateAsync<bool>("window.__ambient.track.enabled"));

        // Out of sight, the same: paused, and it stays paused when the page is seen again until the clinician resumes.
        await page.EvaluateAsync("""
            Object.defineProperty(document, 'visibilityState', { value: 'hidden', configurable: true });
            document.dispatchEvent(new Event('visibilitychange'));
            """);
        Assert.False(await page.EvaluateAsync<bool>("window.__ambient.track.enabled"));
        var hidden = await AmbientBrowserTests.ChunksAsync(page);
        await page.WaitForTimeoutAsync(400);
        await page.EvaluateAsync("""
            Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true });
            document.dispatchEvent(new Event('visibilitychange'));
            """);
        await page.WaitForTimeoutAsync(200);
        Assert.Equal(hidden, await AmbientBrowserTests.ChunksAsync(page));
        Assert.Contains("Paused while this page was out of sight", await page.TextContentAsync("[data-ambient-status]"));
        await page.FocusAsync("[data-ambient-pause]");
        await page.Keyboard.PressAsync("Enter");

        // Stopped: each voice is shown with no role chosen, and the note can't be drafted until every one has one.
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForSelectorAsync("[data-ambient-voice='2']");
        Assert.Equal(["", ""], await page.EvalOnSelectorAllAsync<string[]>("[data-ambient-voice]", "selects => selects.map(s => s.value)"));
        Assert.True(await page.IsDisabledAsync("[data-ambient-draft]"));
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('[data-ambient-voice=\"1\"]')"));
        Assert.True(await page.EvaluateAsync<bool>("window.__ambient.track.stopped"));
        await page.Keyboard.TypeAsync("M");
        Assert.True(await page.IsDisabledAsync("[data-ambient-draft]"));
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync("T");
        Assert.True(await page.IsEnabledAsync("[data-ambient-draft]"));
        await AiReviewBrowserTests.TabToAsync(page, "[data-ambient-draft]");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/review/*");
        var named = Assert.Single(apps.Dictation.Received, r => r.PathAndQuery.EndsWith("/speakers", StringComparison.Ordinal));
        Assert.Equal("""{"speakers":{"1":"clinician","2":"patient"}}""", System.Text.Encoding.UTF8.GetString(named.Body));

        // The review: the transcript a turn at a time with who said it, and the patient's own words said to be so.
        await page.WaitForSelectorAsync(".sc-transcript__speaker");
        Assert.Equal(["Clinician", "Patient", "Clinician"], (await page.Locator(".sc-transcript__speaker").AllTextContentsAsync()).Take(3));
        Assert.True(await page.IsVisibleAsync("[data-ambient-own-words]"));
        Assert.Contains("In the patient's own words.", await page.TextContentAsync("[data-dictation-field=subjective]"));
        Assert.Empty(errors);
    }
}
