using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RefillRequestTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-57
// Purpose / scenario: Asking to renew a medication works on a phone and from the keyboard, in the light theme and the
//   dark: at 360 pixels wide nothing scrolls sideways, a long medication name included; the patient reaches a
//   medication with Tab, chooses it with Space, types a note, reaches "Send request" and sends it with Enter, each
//   control showing the focus ring; the button is the portal's 44-pixel control and the text its 18-pixel body.
// Type: Non-functional (usability)
// Requirements: NFR-12, proposed FR-58
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a portal account, signed in; Dr. Test has the patient on their panel. Chromium,
//   driven by Playwright, 360 by 740 pixels, with the keyboard only.
// Inputs and system conditions: GET /portal/refills; key presses only: Tab, Space, letters and Enter.
// Test data: fluticasone propionate 0.05 MG/ACTUAT Metered Dose Nasal Spray, "1 spray in each nostril daily"; in the
//   dark theme, lisinopril 10 MG Oral Tablet.
// Steps:
//   1. In each theme, open the page.
//   2. Press Tab until the medication has the focus, Space, Tab to the note, type "Synthetic note.", Tab to "Send
//      request", and press Enter.
// Expected result: Step 1: the page is no wider than the window; the button is at least 44 pixels tall and the page's
//   text 18 pixels. Step 2: the medication's radio button, the note and the button each show the focus ring; the page
//   that follows is /portal/refills?sent=true, saying "Sent:", no wider than the window. No script errors.
// Creator: Tahseen Arnan
//
// In the browser tests' collection, which doesn't run beside the others (AiReviewBrowserTests says why).
[Collection(BrowserTests.Collection)]
public class TC_UC6_57_Nfr12_RefillSmallScreen(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task At_360_pixels_in_both_themes_a_request_is_sent_from_the_keyboard()
    {
        var (_, _, patient) = await PortalPatientAsync(apps, "Phonerefill");
        await MedicationAsync(apps, patient.Patient, "fluticasone propionate 0.05 MG/ACTUAT Metered Dose Nasal Spray", "1797907", dosage: "1 spray in each nostril daily");
        await MedicationAsync(apps, patient.Patient);

        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        var errors = new List<string>();
        const string fits = "document.documentElement.scrollWidth <= window.innerWidth";
        const string focusRing = "getComputedStyle(document.activeElement).outlineStyle !== 'none'";

        foreach (var (scheme, medication) in new[] { (ColorScheme.Light, "fluticasone"), (ColorScheme.Dark, "lisinopril") })
        {
            await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ColorScheme = scheme, ViewportSize = new() { Width = 360, Height = 740 } });
            await context.AddCookiesAsync(patient.Jar.GetCookies(Base).Cast<System.Net.Cookie>().Select(c => new Microsoft.Playwright.Cookie
            { Name = c.Name, Value = c.Value, Domain = "localhost", Path = c.Path, Secure = true, HttpOnly = c.HttpOnly, SameSite = SameSiteAttribute.Strict }));
            var page = await context.NewPageAsync();
            page.PageError += (_, error) => errors.Add(error);

            await page.GotoAsync($"{server.Url}{PortalPath}");
            Assert.True(await page.EvaluateAsync<bool>(fits), $"The page scrolls sideways at 360 pixels in the {scheme} theme.");
            Assert.True(await page.EvaluateAsync<double>("document.querySelector('[data-refill-form=ask] button[type=submit]').getBoundingClientRect().height") >= 44);
            Assert.Equal("18px", await page.EvaluateAsync<string>("getComputedStyle(document.querySelector('main')).fontSize"));

            // The radio buttons are one group: Tab reaches it, and the arrow keys move to the medication's.
            await AiReviewBrowserTests.TabToAsync(page, "[data-refill-form=ask] input[name=Medication]");
            while (!await page.EvaluateAsync<bool>("name => document.activeElement.parentElement.textContent.includes(name)", medication))
            {
                await page.Keyboard.PressAsync("ArrowDown");
            }

            Assert.True(await page.EvaluateAsync<bool>(focusRing), "The medication's radio button shows no focus ring.");
            await page.Keyboard.PressAsync("Space");
            await AiReviewBrowserTests.TabToAsync(page, "#refill-note");
            Assert.True(await page.EvaluateAsync<bool>(focusRing), "The note shows no focus ring.");
            await page.Keyboard.TypeAsync("Synthetic note.");
            await AiReviewBrowserTests.TabToAsync(page, "[data-refill-form=ask] button[type=submit]");
            Assert.True(await page.EvaluateAsync<bool>(focusRing), "Send request shows no focus ring.");
            await page.Keyboard.PressAsync("Enter");
            await page.WaitForURLAsync(url => url.EndsWith("/portal/refills?sent=true", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("Sent:", await page.Locator("[data-refill-sent]").TextContentAsync());
            Assert.True(await page.EvaluateAsync<bool>(fits), $"The page after sending scrolls sideways at 360 pixels in the {scheme} theme.");
        }

        Assert.Equal(2, (await RequestsAsync(apps, patient.Patient)).Count);
        Assert.All(await RequestsAsync(apps, patient.Patient), request => Assert.Contains("|Synthetic note.|", request));
        Assert.Empty(errors);
    }
}
