using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RecordDownload;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-33
// Purpose / scenario: "Download my record" works on a phone and from the keyboard, and its printable page prints as the
//   record alone. At 360 pixels wide neither page scrolls sideways; the patient ticks "Include sensitive items", downloads
//   the file and opens the printable page with Tab, Space and Enter, the focus ring showing on each control. Printed, in
//   the light theme or the dark, a page has no app bar, button or anything meant for the screen, and never says that
//   sensitive items were left out; its text is the brand navy on no ground; a table row isn't split; and nothing is
//   wider than the paper. On screen, the print rules change nothing. The bands under the app bar print: a proxy's
//   printout says whose record it is, and an off-site page that it is read-only. A clinician's page prints sanely too:
//   no app bar, and its tables not clipped.
// Type: Non-functional (usability)
// Requirements: NFR-12, proposed FR-56
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: As TC-UC6-24, the patient signed in; Dr. Keyboard Print, with the patient on their panel, signed in;
//   later, someone the patient gives access to, signed in and looking at the record. The off-site app can't reach the
//   clinic's server.
//   Chromium, driven by Playwright: 360 by 740 pixels for the phone; 794 pixels wide, A4's width, for paper.
// Inputs and system conditions: GET /portal/record and /portal/record/print?sensitive=true; key presses only: Tab, Space
//   and Enter; the print media type emulated, with the light and the dark color scheme; /portal/record/print without
//   sensitive items, printed; /portal/results, /portal/appointments, /portal/summaries and /portal/record/print as the
//   proxy, printed; GET /patients/{id}/vitals as the clinician, printed, and /patients/{id} off-site, printed.
// Test data: FhirRequests.ChartAsync's record.
// Steps:
//   1. Open Download my record at 360 pixels.
//   2. Tab to "Include sensitive items" and press Space; Tab to "Download file" and press Enter.
//   3. Tab to "Open printable page" and press Enter.
//   4. At 794 pixels, emulate print, light then dark; then screen, light then dark; then the printable page without
//      sensitive items, on screen and printed.
//   5. As the proxy, open each portal page and emulate print.
//   6. As the clinician, open the patient's vital signs and emulate print; then the chart off-site, printed.
// Expected result: Step 1: no sideways scroll. Step 2: the focus ring on each; a download named health-record-<date>.json
//   whose file holds sertraline. Step 3: /portal/record/print?sensitive=true, with "Sensitive items shown.", no sideways
//   scroll. Step 4: printed, the app bar, the sign-out button and "use your browser's Print command" aren't displayed;
//   the body's text is rgb(14, 27, 44) on a transparent ground, in the light scheme, in both themes; a table row avoids a break; nothing
//   overflows. On screen, the light theme's text is rgb(14, 27, 44) on white and the dark theme's rgb(232, 238, 243).
//   Without sensitive items, "Sensitive items hidden." is displayed on screen and not printed, and the paper says
//   nothing of sensitive items. Step 5: each printout keeps the band "Shared record. You're viewing Synthetic Fhirchart's
//   record, not your own.", without its button. Step 6: no app bar, the patient banner not stuck, the flowsheet's
//   wrapper not clipping it; off-site, the band "Read-only. The clinic's server can't be reached, so nothing can be
//   saved until it's back." prints. No script errors.
// Creator: Tahseen Arnan
//
// In the browser tests' collection, which doesn't run beside the others (AiReviewBrowserTests says why).
[Collection(BrowserTests.Collection)]
public class TC_UC6_33_Nfr12_DownloadRecordSmallScreenAndPrint(WebApps apps) : IClassFixture<WebApps>
{
    const string Fits = "document.documentElement.scrollWidth <= window.innerWidth";

    const string FocusRing = "getComputedStyle(document.activeElement).outlineStyle !== 'none'";

    [Fact]
    public async Task The_download_works_at_360_pixels_from_the_keyboard_and_prints_as_the_record_alone()
    {
        var (chart, patient) = await PortalChartAsync(apps, "Dr. Keyboard Print");

        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = 360, Height = 740 } });
        await context.AddCookiesAsync(patient.Jar.GetCookies(Base).Cast<System.Net.Cookie>().Select(c => new Microsoft.Playwright.Cookie
        { Name = c.Name, Value = c.Value, Domain = "localhost", Path = c.Path, Secure = true, HttpOnly = c.HttpOnly, SameSite = SameSiteAttribute.Strict }));
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);

        await page.GotoAsync($"{server.Url}{PagePath}");
        Assert.True(await page.EvaluateAsync<bool>(Fits), "Download my record scrolls sideways at 360 pixels.");
        await AiReviewBrowserTests.TabToAsync(page, "#record-sensitive");
        Assert.True(await page.EvaluateAsync<bool>(FocusRing));
        await page.Keyboard.PressAsync("Space");
        Assert.True(await page.IsCheckedAsync("#record-sensitive"));
        await page.Keyboard.PressAsync("Tab");
        Assert.Equal("Download file", await page.EvaluateAsync<string>("document.activeElement.textContent"));
        Assert.True(await page.EvaluateAsync<bool>(FocusRing));
        // The file is named by the UTC day of the request: the day read just before it, or after it if midnight came
        // between.
        var before = DateTime.UtcNow;
        var download = await page.RunAndWaitForDownloadAsync(() => page.Keyboard.PressAsync("Enter"));
        var after = DateTime.UtcNow;
        Assert.Contains(download.SuggestedFilename, new[] { $"health-record-{before:yyyy-MM-dd}.json", $"health-record-{after:yyyy-MM-dd}.json" });
        Assert.Contains("sertraline", await System.IO.File.ReadAllTextAsync(await download.PathAsync(), TestContext.Current.CancellationToken));

        await page.Keyboard.PressAsync("Tab");
        Assert.Equal("Open printable page", await page.EvaluateAsync<string>("document.activeElement.textContent"));
        Assert.True(await page.EvaluateAsync<bool>(FocusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync(url => url.EndsWith("/portal/record/print?sensitive=true", StringComparison.Ordinal));
        Assert.Contains("Sensitive items shown.", await page.Locator("[data-sensitive=shown]").TextContentAsync());
        Assert.True(await page.EvaluateAsync<bool>(Fits), "The printable page scrolls sideways at 360 pixels.");

        // On paper, A4's width at 96 pixels an inch, in either theme.
        await page.SetViewportSizeAsync(794, 1123);
        foreach (var scheme in new[] { ColorScheme.Light, ColorScheme.Dark })
        {
            await page.EmulateMediaAsync(new() { Media = Media.Print, ColorScheme = scheme });
            Assert.Equal("none", await DisplayAsync(page, ".sc-appbar"));
            Assert.Equal("none", await DisplayAsync(page, ".sc-appbar__signout"));
            Assert.Equal("none", await DisplayAsync(page, "main .sc-screen-only"));
            Assert.NotEqual("none", await DisplayAsync(page, "[data-sensitive=shown]"));
            Assert.Equal("rgb(14, 27, 44)", await page.EvaluateAsync<string>("getComputedStyle(document.body).color"));
            Assert.Equal("rgba(0, 0, 0, 0)", await page.EvaluateAsync<string>("getComputedStyle(document.body).backgroundColor"));
            Assert.Equal("light", await page.EvaluateAsync<string>("getComputedStyle(document.documentElement).colorScheme"));
            Assert.Equal("rgba(0, 0, 0, 0)", await page.EvaluateAsync<string>("getComputedStyle(document.querySelector('.sc-panel')).backgroundColor"));
            Assert.Equal("avoid", await page.EvaluateAsync<string>("getComputedStyle(document.querySelector('[data-record=vitals] tbody tr')).breakInside"));
            Assert.True(await page.EvaluateAsync<bool>(Fits), $"The printed page ({scheme}) is wider than the paper.");
        }

        // On screen the print rules change nothing.
        await page.EmulateMediaAsync(new() { Media = Media.Screen, ColorScheme = ColorScheme.Light });
        Assert.Equal("rgb(14, 27, 44)", await page.EvaluateAsync<string>("getComputedStyle(document.body).color"));
        Assert.Equal("rgb(244, 246, 248)", await page.EvaluateAsync<string>("getComputedStyle(document.body).backgroundColor"));
        Assert.NotEqual("none", await DisplayAsync(page, ".sc-appbar"));
        await page.EmulateMediaAsync(new() { Media = Media.Screen, ColorScheme = ColorScheme.Dark });
        Assert.Equal("rgb(232, 238, 243)", await page.EvaluateAsync<string>("getComputedStyle(document.body).color"));

        // Without sensitive items: on screen the page says it left some out; paper never does.
        await page.GotoAsync($"{server.Url}{PrintPath}");
        Assert.NotEqual("none", await DisplayAsync(page, "[data-sensitive=hidden]"));
        await page.EmulateMediaAsync(new() { Media = Media.Print, ColorScheme = ColorScheme.Light });
        Assert.Equal("none", await DisplayAsync(page, "[data-sensitive=hidden]"));
        Assert.DoesNotContain("Sensitive items", await page.EvaluateAsync<string>("document.body.innerText"));
        Assert.Empty(errors);

        // A proxy's printout of a portal page says whose record it is: the band prints, without its button.
        var (proxy, _) = await VisitSummaryTests.ProxyForAsync(apps, patient, "Printproxy", sensitive: false);
        await using var proxyContext = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = 794, Height = 1123 } });
        await proxyContext.AddCookiesAsync(proxy.Jar.GetCookies(Base).Cast<System.Net.Cookie>().Select(c => new Microsoft.Playwright.Cookie
        { Name = c.Name, Value = c.Value, Domain = "localhost", Path = c.Path, Secure = true, HttpOnly = c.HttpOnly, SameSite = SameSiteAttribute.Strict }));
        var proxyPage = await proxyContext.NewPageAsync();
        foreach (var path in new[] { "/portal/results", "/portal/appointments", "/portal/summaries", PrintPath })
        {
            await proxyPage.GotoAsync($"{server.Url}{path}");
            await proxyPage.EmulateMediaAsync(new() { Media = Media.Print });
            Assert.NotEqual("none", await DisplayAsync(proxyPage, "[data-acting]"));
            Assert.Contains("Shared record. You're viewing Synthetic Fhirchart's record, not your own.", await proxyPage.Locator("[data-acting]").InnerTextAsync());
            Assert.Equal("none", await DisplayAsync(proxyPage, "[data-acting] button"));
        }

        // A clinician's page, printed: the vital signs flowsheet, the widest table the app has. No app bar, the banner
        // in its place rather than stuck to the top of each sheet, and the table's wrapper clipping nothing.
        await using var staff = await AiReviewBrowserTests.ContextAsync(browser, chart.Session);
        var vitals = await staff.NewPageAsync();
        await vitals.SetViewportSizeAsync(794, 1123);
        await vitals.GotoAsync($"{server.Url}{VitalTests.Path(chart.Patient)}");
        await vitals.EmulateMediaAsync(new() { Media = Media.Print });
        Assert.Equal("none", await DisplayAsync(vitals, ".sc-appbar"));
        Assert.Equal("static", await vitals.EvaluateAsync<string>("getComputedStyle(document.querySelector('.sc-banner')).position"));
        Assert.Equal("visible", await vitals.EvaluateAsync<string>("getComputedStyle(document.querySelector('.sc-table-wrap')).overflowX"));

        // Off-site, with the clinic's server out of reach, a printed page keeps its read-only band: the copy may be behind.
        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(TestContext.Current.CancellationToken));
        await using var offSite = await ClinicalBrowserHost.StartAsync(apps, apps.CloudCutOff);
        var chartOffSite = await staff.NewPageAsync();
        await chartOffSite.SetViewportSizeAsync(794, 1123);
        await chartOffSite.GotoAsync($"{offSite.Url}/patients/{chart.Patient}");
        await chartOffSite.EmulateMediaAsync(new() { Media = Media.Print });
        Assert.Equal("none", await DisplayAsync(chartOffSite, ".sc-appbar"));
        Assert.NotEqual("none", await DisplayAsync(chartOffSite, "p.sc-notice--band"));
        Assert.StartsWith("Read-only. The clinic's server can't be reached, so nothing can be saved until it's back.", await chartOffSite.Locator("p.sc-notice--band").InnerTextAsync());
    }

    static Task<string> DisplayAsync(IPage page, string selector) =>
        page.EvaluateAsync<string>("selector => getComputedStyle(document.querySelector(selector)).display", selector);
}
