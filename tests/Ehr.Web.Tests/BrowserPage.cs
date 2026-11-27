using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Playwright;

namespace Ehr.Web.Tests;

// A page in Chromium on an app a ClinicalBrowserHost serves: the host, Playwright, the browser, the context and the page
// a browser test opens for itself, and the errors the page's scripts throw. Chromium is launched by
// AiReviewBrowserTests.LaunchAsync, so the test is skipped where it isn't installed. Disposed as `using` lines in the
// test would dispose them, the last opened first: the context, the browser, Playwright, then the host; and whatever was
// opened is disposed if a later step fails.
sealed class BrowserPage : IAsyncDisposable
{
    ClinicalBrowserHost? server;
    IPlaywright? playwright;
    IBrowser? browser;
    IBrowserContext? context;
    IPage page = null!;
    readonly List<string> errors = [];

    // The host for `app` (the on-site app if none), and a page in the context `newContext` makes in the browser.
    public static async Task<BrowserPage> OpenAsync(WebApps apps, Func<IBrowser, Task<IBrowserContext>> newContext, WebApplicationFactory<Program>? app = null)
    {
        var opened = new BrowserPage();
        try
        {
            opened.server = await ClinicalBrowserHost.StartAsync(apps, app);
            opened.playwright = await Playwright.CreateAsync();
            opened.browser = await AiReviewBrowserTests.LaunchAsync(opened.playwright);
            opened.context = await newContext(opened.browser);
            opened.page = await opened.context.NewPageAsync();
            opened.page.PageError += (_, error) => opened.errors.Add(error);
            return opened;
        }
        catch
        {
            await opened.DisposeAsync();
            throw;
        }
    }

    public void Deconstruct(out ClinicalBrowserHost server, out IPage page, out List<string> errors) =>
        (server, page, errors) = (this.server!, this.page, this.errors);

    public async ValueTask DisposeAsync()
    {
        await using (server)
        using (playwright)
        await using (browser)
        await using (context)
        {
        }
    }
}
