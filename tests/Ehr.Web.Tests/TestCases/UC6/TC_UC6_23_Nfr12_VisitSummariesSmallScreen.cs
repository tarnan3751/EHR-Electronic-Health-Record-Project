using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-23
// Purpose / scenario: The visit summaries page works on a phone and from the keyboard: at 360 pixels wide nothing
//   scrolls sideways, before or after sensitive summaries are shown, and the patient reaches "Show sensitive summaries"
//   with Tab, sees the focus ring on it, and opens it with Enter. The notice names its state in words and the link says
//   what it does, for a screen reader. In the light theme and the dark, a withdrawn summary's placeholder is a card
//   padded as a summary's card is, on the same surface.
// Type: Non-functional (usability)
// Requirements: NFR-12, proposed FR-55
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a portal account, signed in. Dr. Test, with the patient on their panel, has released
//   a summary with a long word in it, one ticked "Sensitive", and withdrawn a third. Chromium, driven by Playwright, 360
//   by 740 pixels, with the keyboard only.
// Inputs and system conditions: GET /portal/summaries; key presses only: Tab and Enter.
// Test data: A What we found with a 60-letter word.
// Steps:
//   1. Open Visit summaries.
//   2. Press Tab until "Show sensitive summaries" has the focus, and press Enter.
//   3. Open Visit summaries again, in the light theme and in the dark.
// Expected result: Step 1: the page is no wider than the window; the notice reads "Sensitive items hidden:" and its
//   link "Show sensitive summaries". Step 2: the focused link shows the focus ring; Enter opens
//   /portal/summaries?sensitive=true, which shows the sensitive summary and is no wider than the window either. Step 3:
//   in each theme the page is no wider than the window, the summary's card and the placeholder are both padded by the
//   brand's space-4 on every side (the card's padding at this width), and the placeholder's background is the summary
//   card's, which differs between the themes. No script errors.
// Creator: Tahseen Arnan
//
// In the browser tests' collection, which doesn't run beside the others (AiReviewBrowserTests says why).
[Collection(BrowserTests.Collection)]
public class TC_UC6_23_Nfr12_VisitSummariesSmallScreen(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task At_360_pixels_nothing_scrolls_sideways_and_sensitive_summaries_open_from_the_keyboard()
    {
        var (_, session, patient) = await PortalPatientAsync(apps, "Phone");
        var notes = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            notes.Add(await SignedNoteAsync(session.Client, patient.Patient));
        }

        using (var released = await ReleaseAsync(session.Client, patient.Patient, notes[0], found: "Your chest sounded clear: " + new string('a', 60) + "."))
        {
            Assert.Contains("Released.", Words(await ReadAsync(released)));
        }

        using (var released = await ReleaseAsync(session.Client, patient.Patient, notes[1], "We talked about something private.", sensitive: true))
        {
            Assert.Contains("Released.", Words(await ReadAsync(released)));
        }

        using (var released = await ReleaseAsync(session.Client, patient.Patient, notes[2]))
        {
            Assert.Contains("Released.", Words(await ReadAsync(released)));
        }

        using (var withdrawn = await WithdrawAsync(session.Client, patient.Patient, notes[2], await StandingAsync(apps, patient.Patient)))
        {
            Assert.Contains("Withdrawn.", Words(await ReadAsync(withdrawn)));
        }

        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = 360, Height = 740 } });
        await context.AddCookiesAsync(patient.Jar.GetCookies(Base).Cast<System.Net.Cookie>().Select(c => new Microsoft.Playwright.Cookie
        { Name = c.Name, Value = c.Value, Domain = "localhost", Path = c.Path, Secure = true, HttpOnly = c.HttpOnly, SameSite = SameSiteAttribute.Strict }));
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        const string fits = "document.documentElement.scrollWidth <= window.innerWidth";
        const string focusRing = "getComputedStyle(document.activeElement).outlineStyle !== 'none'";

        await page.GotoAsync($"{server.Url}/portal/summaries");
        Assert.True(await page.EvaluateAsync<bool>(fits), "The page scrolls sideways at 360 pixels.");
        Assert.Contains("Sensitive items hidden:", await page.Locator("[data-sensitive=hidden]").TextContentAsync());
        await AiReviewBrowserTests.TabToAsync(page, "a[href='/portal/summaries?sensitive=true']");
        Assert.Equal("Show sensitive summaries", await page.EvaluateAsync<string>("document.activeElement.textContent"));
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync(url => url.EndsWith("/portal/summaries?sensitive=true", StringComparison.Ordinal));
        Assert.Contains("We talked about something private.", await page.Locator("main").TextContentAsync());
        Assert.True(await page.EvaluateAsync<bool>(fits), "The page with sensitive summaries scrolls sideways at 360 pixels.");

        // In both themes: the placeholder is padded as the summary's card is, by the card's padding at this width.
        var surfaces = new List<string>();
        foreach (var scheme in new[] { ColorScheme.Light, ColorScheme.Dark })
        {
            await using var themed = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ColorScheme = scheme, ViewportSize = new() { Width = 360, Height = 740 } });
            await themed.AddCookiesAsync(patient.Jar.GetCookies(Base).Cast<System.Net.Cookie>().Select(c => new Microsoft.Playwright.Cookie
            { Name = c.Name, Value = c.Value, Domain = "localhost", Path = c.Path, Secure = true, HttpOnly = c.HttpOnly, SameSite = SameSiteAttribute.Strict }));
            var shown = await themed.NewPageAsync();
            shown.PageError += (_, error) => errors.Add(error);
            await shown.GotoAsync($"{server.Url}/portal/summaries");
            Assert.True(await shown.EvaluateAsync<bool>(fits), $"The page scrolls sideways at 360 pixels in the {scheme} theme.");
            var space = (await shown.EvaluateAsync<string>("getComputedStyle(document.documentElement).getPropertyValue('--space-4')")).Trim();
            var padded = $"{space} {space} {space} {space}";
            Assert.Equal([$"standing {padded}", $"withdrawn {padded}"], await shown.EvaluateAsync<string[]>(
                """
                [...document.querySelectorAll('[data-summary]')].map(card => {
                    const style = getComputedStyle(card);
                    return `${card.dataset.summary} ${style.paddingTop} ${style.paddingRight} ${style.paddingBottom} ${style.paddingLeft}`;
                })
                """));
            // On the card surface, as the summary's card is.
            var (placeholder, summary) = (await shown.EvaluateAsync<string>("getComputedStyle(document.querySelector('[data-summary=withdrawn]')).backgroundColor"),
                await shown.EvaluateAsync<string>("getComputedStyle(document.querySelector('[data-summary=standing]')).backgroundColor"));
            Assert.Equal(summary, placeholder);
            surfaces.Add(placeholder);
        }

        Assert.NotEqual(surfaces[0], surfaces[1]);
        Assert.Empty(errors);
    }
}
