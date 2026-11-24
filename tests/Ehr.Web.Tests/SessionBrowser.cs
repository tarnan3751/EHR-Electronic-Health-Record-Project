using Ehr.Testing;
using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The end of a session in a real browser (the expansion plan's step 18; session.js): helpers for UC 11's test cases.
// The browser's clock is Playwright's, installed before the page's scripts run, and the server's is the fixture's
// (ShiftedClock): Idle moves both on together, as the time a person is away passes for both.
static class SessionBrowser
{
    public const string Warning = "dialog[data-session-warning]";

    public const string OpenWarning = "dialog[data-session-warning][open]";

    public const string Frame = "[data-session-frame]";

    // The values of a portal page's session settings (the layout's warning and what replaces the page): the same on
    // every portal page and for everyone, none an identifier, for the tests that hold a page's data attributes to a list.
    public static readonly string[] PortalSettings =
    [
        "portal", "/portal/session", "900", "120", "/portal/sign-in?ReturnUrl=%2Fportal%2Fsession", "/portal", "/portal/choose",
        "/portal/acting", "idle", "longest", "ended", "signed-out",
    ];

    // A browser context holding the session's cookies, with the clock installed and, for recordings, the fake microphone
    // and recorder the ambient tests use.
    public static async Task<IBrowserContext> ContextAsync(IBrowser browser, Session session, bool microphone = false, BrowserNewContextOptions? options = null)
    {
        var context = await browser.NewContextAsync(options ?? new() { IgnoreHTTPSErrors = true });
        await context.AddCookiesAsync(session.Jar.GetCookies(Base).Cast<System.Net.Cookie>().Select(c => new Cookie
        { Name = c.Name, Value = c.Value, Domain = "localhost", Path = c.Path, Secure = true, HttpOnly = c.HttpOnly, SameSite = SameSiteAttribute.Strict }));
        if (microphone)
        {
            await context.AddInitScriptAsync(AmbientBrowserTests.FakeMicrophone);
        }

        await context.Clock.InstallAsync();
        return context;
    }

    // Time away: the server's clock and the browser's both move on, and the browser's timers that fell due run once.
    public static async Task IdleAsync(WebApps apps, IPage page, TimeSpan time)
    {
        apps.Clock.Pass(time);
        await page.Clock.FastForwardAsync((long)time.TotalMilliseconds);
    }

    // Time passing with the page at work: the server's clock and the browser's moved on a minute at a time, each step
    // letting the page's timers that fell due run once, so a page that sends keep-alives or audio does so every minute.
    public static async Task StepAsync(WebApps apps, IPage page, TimeSpan time)
    {
        for (var passed = TimeSpan.Zero; passed < time; passed += TimeSpan.FromMinutes(1))
        {
            await IdleAsync(apps, page, TimeSpan.FromMinutes(1) < time - passed ? TimeSpan.FromMinutes(1) : time - passed);
            await page.WaitForTimeoutAsync(200);
        }
    }

    // Whether the warning has ever opened on the page since this was called.
    public static Task WatchWarningAsync(IPage page) => page.EvaluateAsync($$"""
        () => {
            window.__warned = false;
            const dialog = document.querySelector('{{Warning}}');
            new MutationObserver(() => { if (dialog.open) window.__warned = true; }).observe(dialog, { attributes: true });
        }
        """);

    public static Task<bool> WarnedAsync(IPage page) => page.EvaluateAsync<bool>("window.__warned === true");

    // Signs in again in the frame the signed-out page offers, from the keyboard: the email, the password, then the code.
    public static async Task SignInAgainAsync(IPage page, StaffAccount account)
    {
        await AiReviewBrowserTests.TabToAsync(page, "[data-session-sign-in]");
        await page.Keyboard.PressAsync("Enter");
        var frame = page.FrameLocator(Frame);
        await frame.Locator("#Input_Email").WaitForAsync();
        await frame.Locator("#Input_Email").FillAsync(account.Email);
        await frame.Locator("#Input_Password").FillAsync(account.Password);
        await frame.Locator("#Input_Password").PressAsync("Enter");
        await frame.Locator("#Code").WaitForAsync();
        await frame.Locator("#Code").FillAsync(Totp.Code(account.AuthenticatorKey));
        await frame.Locator("#Code").PressAsync("Enter");
    }

    // The same, for a portal account: the email and the password.
    public static async Task SignInAgainAsync(IPage page, string email, string password)
    {
        await AiReviewBrowserTests.TabToAsync(page, "[data-session-sign-in]");
        await page.Keyboard.PressAsync("Enter");
        var frame = page.FrameLocator(Frame);
        await frame.Locator("#Input_Email").WaitForAsync();
        await frame.Locator("#Input_Email").FillAsync(email);
        await frame.Locator("#Input_Password").FillAsync(password);
        await frame.Locator("#Input_Password").PressAsync("Enter");
    }

    // How long the browser's own session has left, as the server says, asked with a check that renews nothing.
    public static Task<int> LeftAsync(IPage page, string path = "/session") => page.EvaluateAsync<int>(
        "path => fetch(path, { headers: { 'Ehr-Session-Check': '1' } }).then(r => r.json()).then(a => a.left)", path);

    // Whether the server has ended the browser's session: its session page sends it to sign in.
    public static Task<bool> EndedAsync(IPage page, string path = "/session") => page.EvaluateAsync<bool>(
        "path => fetch(path, { redirect: 'manual' }).then(r => r.type === 'opaqueredirect')", path);

    // What a person sees on the page: its text, its title, and what is typed in its fields.
    public static Task<string> SeenAsync(IPage page) => page.EvaluateAsync<string>(
        "() => document.title + '\\n' + document.body.innerText + '\\n' + [...document.querySelectorAll('input, textarea')].map(f => f.value).join('\\n')");
}
