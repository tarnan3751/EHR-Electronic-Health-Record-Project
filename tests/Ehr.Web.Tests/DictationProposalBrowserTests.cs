using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.AiReviewBrowserTests;
using static Ehr.Web.Tests.DictationProposalTests;

namespace Ehr.Web.Tests;

// The expansion plan's step 15 in a real browser: what a dictation proposes, on its review and the form opened from it,
// from the keyboard (NFR-12), and at every width in both themes. In the browser tests' collection, for the reason
// AiReviewBrowserTests gives.
[Collection(BrowserTests.Collection)]
public class DictationProposalBrowserTests(WebApps apps) : IClassFixture<WebApps>
{
    // TC-UC13-12 (NFR-12): with the keyboard only, the clinician moves into the order's proposal, which shows its words in
    // the transcript, opens it in the order form, signs a lab order there, and is back on the review with the cursor on
    // the allergy's proposal; dismisses that, and the cursor is on the section's heading, nothing waiting.
    [Fact]
    public async Task TC_UC13_12_a_proposed_order_is_opened_and_signed_and_the_next_dismissed_from_the_keyboard()
    {
        var dictated = await DictateAsync(apps);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        await using var context = await ContextAsync(browser, dictated.Session);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync($"{server.Url}{dictated.ReviewPath}");

        var order = $"[data-chart-proposal=\"{dictated.Order}\"] [data-open-proposal]";
        await TabToAsync(page, order);
        Assert.Equal(DictationSidecarStub.OrderSaid, await page.Locator("[data-dictation-transcript] mark.sc-transcript__source").TextContentAsync());
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync($"**/orders/new?proposal={dictated.Order}");
        Assert.Contains("Proposed from dictation. Check every field.", await page.Locator("[data-proposed-from-dictation]").TextContentAsync());
        await TabToAsync(page, "a[href*='type=lab']");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**type=lab&proposal=*");
        await page.WaitForFunctionAsync("document.activeElement.matches('#order-item')");
        Assert.Equal("", await page.Locator("#order-item").InputValueAsync());
        await page.Keyboard.InsertTextAsync("Lipid panel");
        await TabToAsync(page, "#order-indication");
        await page.Keyboard.InsertTextAsync("Synthetic fatigue");
        await TabToAsync(page, "[data-order-sign]");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync($"**/review/{dictated.Review}?decided={dictated.Order}");
        // Once the page's script has run: the cursor on the next proposal waiting.
        await page.WaitForFunctionAsync($"document.activeElement.matches('[data-chart-proposal=\"{dictated.Allergy}\"] [data-open-proposal]')");
        Assert.Equal("Ordered", await page.Locator($"[data-chart-proposal=\"{dictated.Order}\"] [data-chart-proposal-state]").TextContentAsync());

        await TabToAsync(page, $"[data-chart-proposal=\"{dictated.Allergy}\"] button");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync($"**decided={dictated.Allergy}");
        await page.WaitForFunctionAsync("document.activeElement.matches('#chart-proposals')");
        Assert.Equal("Dismissed", await page.Locator($"[data-chart-proposal=\"{dictated.Allergy}\"] [data-chart-proposal-state]").TextContentAsync());
        Assert.StartsWith("ordered|", await StateAsync(apps, dictated.Order), StringComparison.Ordinal);
        Assert.Empty(errors);
    }

    // The review's proposals at 1,280 and 640 pixels, in the light theme and the dark: nothing scrolls sideways, nothing is
    // wider than the window, and a long dictated sentence wraps within its proposal (step 13e's answers broke at the
    // desktop width while its phone test passed).
    [Fact]
    public async Task The_proposals_fit_every_width_in_both_themes_and_a_long_sentence_wraps()
    {
        // The order said at the end of a long sentence.
        apps.Dictation.Reset().ProposalLead = "Since her father had a heart attack at fifty two and her own cholesterol was borderline at the last check "
            + "three years ago, and she would like to know where she stands before deciding on any medicine at all, with her sister also on a statin,";
        var stopped = await DictateAsync(apps, reset: false);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        var errors = new List<string>();
        const string Layout = """
            () => {
                const width = window.innerWidth;
                const words = document.querySelector('[data-chart-proposal] [data-chart-proposal-words]').getBoundingClientRect();
                const line = parseFloat(getComputedStyle(document.querySelector('[data-chart-proposal-words]')).lineHeight);
                const wide = [...document.querySelectorAll('body *')].filter(e => e.getBoundingClientRect().right > width + 1).map(e => e.tagName + '.' + e.className);
                return { scrolls: document.documentElement.scrollWidth > width, wide, wraps: words.height > 2 * line, inside: words.right <= width };
            }
            """;
        foreach (var scheme in new[] { ColorScheme.Light, ColorScheme.Dark })
        {
            foreach (var width in new[] { 1280, 640 })
            {
                await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ColorScheme = scheme, ViewportSize = new() { Width = width, Height = 800 } });
                await context.AddCookiesAsync(stopped.Session.Jar.GetCookies(Browser.Base).Cast<System.Net.Cookie>().Select(c => new Microsoft.Playwright.Cookie
                { Name = c.Name, Value = c.Value, Domain = "localhost", Path = c.Path, Secure = true, HttpOnly = c.HttpOnly, SameSite = SameSiteAttribute.Strict }));
                var page = await context.NewPageAsync();
                page.PageError += (_, error) => errors.Add(error);
                await page.GotoAsync($"{server.Url}{stopped.ReviewPath}");
                var layout = await page.EvaluateAsync<System.Text.Json.JsonElement>(Layout);
                var where = $"at {width} pixels in the {scheme} theme";
                Assert.False(layout.GetProperty("scrolls").GetBoolean(), $"The review scrolls sideways {where}.");
                Assert.True(layout.GetProperty("wide").GetArrayLength() == 0, $"Wider than the window {where}: {layout.GetProperty("wide")}");
                Assert.True(layout.GetProperty("wraps").GetBoolean(), $"The long sentence doesn't wrap {where}.");
                Assert.True(layout.GetProperty("inside").GetBoolean(), $"The sentence runs past the window {where}.");
            }
        }

        Assert.Empty(errors);
    }

    // Step 15.1, at the owner's word (2026-11-24): what was heard after a proposal is in view beside the way into its
    // form, with no click, at 1,280 and 640 pixels in the light theme and the dark: a refusal the service's rules don't
    // read ("Order a CBC. She turned down the blood test.", a trick sentence the owner accepted by name), from the
    // service's own answer pinned in src/Ehr.Dictation/contract, shown under the words as heard and above the button.
    [Fact]
    public async Task What_was_heard_after_a_proposal_is_in_view_beside_its_button_at_every_width_in_both_themes()
    {
        var body = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(
            Ehr.Testing.RepositoryRoot.Combine("src/Ehr.Dictation/contract/result-200-done-proposal_heard_after.json"),
            TestContext.Current.CancellationToken))!["body"]!;
        var stopped = await StopAsync(apps);
        apps.Dictation.Answer = (System.Net.HttpStatusCode.OK, body.ToJsonString());
        using (var staged = await StageAsync(stopped))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, staged.StatusCode);
        }

        apps.Dictation.Answer = null;
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        var errors = new List<string>();
        const string Layout = """
            () => {
                const item = document.querySelector('[data-chart-proposal]');
                const heard = item.querySelector('[data-chart-proposal-heard-after]');
                const after = heard.getBoundingClientRect(), words = item.querySelector('[data-chart-proposal-words]').getBoundingClientRect();
                const button = item.querySelector('[data-open-proposal]').getBoundingClientRect();
                return { text: heard.textContent, below: after.top >= words.bottom, above: after.bottom <= button.top,
                    beside: button.top - after.bottom < 64, inside: after.right <= window.innerWidth && button.right <= window.innerWidth,
                    scrolls: document.documentElement.scrollWidth > window.innerWidth };
            }
            """;
        foreach (var scheme in new[] { ColorScheme.Light, ColorScheme.Dark })
        {
            foreach (var width in new[] { 1280, 640 })
            {
                await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ColorScheme = scheme, ViewportSize = new() { Width = width, Height = 800 } });
                await context.AddCookiesAsync(stopped.Session.Jar.GetCookies(Browser.Base).Cast<System.Net.Cookie>().Select(c => new Microsoft.Playwright.Cookie
                { Name = c.Name, Value = c.Value, Domain = "localhost", Path = c.Path, Secure = true, HttpOnly = c.HttpOnly, SameSite = SameSiteAttribute.Strict }));
                var page = await context.NewPageAsync();
                page.PageError += (_, error) => errors.Add(error);
                await page.GotoAsync($"{server.Url}/patients/{stopped.Patient}/notes/drafts/{stopped.Draft}/review/{stopped.Review}");
                Assert.True(await page.Locator("[data-chart-proposal] [data-chart-proposal-heard-after]").First.IsVisibleAsync());
                var layout = await page.EvaluateAsync<System.Text.Json.JsonElement>(Layout);
                var where = $"at {width} pixels in the {scheme} theme";
                Assert.Equal("Heard after it: She turned down the blood test.", layout.GetProperty("text").GetString());
                Assert.True(layout.GetProperty("below").GetBoolean(), $"What was heard after it isn't under its words {where}.");
                Assert.True(layout.GetProperty("above").GetBoolean() && layout.GetProperty("beside").GetBoolean(),
                    $"What was heard after it isn't just above its button {where}.");
                Assert.True(layout.GetProperty("inside").GetBoolean(), $"It runs past the window {where}.");
                Assert.False(layout.GetProperty("scrolls").GetBoolean(), $"The review scrolls sideways {where}.");
            }
        }

        Assert.Empty(errors);
    }
}
