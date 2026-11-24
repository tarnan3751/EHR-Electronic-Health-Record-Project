using System.Net;
using Microsoft.Playwright;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.SessionBrowser;

namespace Ehr.Web.Tests.TestCases.UC11;

// Test Case ID: TC-UC11-12
// Purpose / scenario: The portal's warning and end, on a phone, from the keyboard (the expansion plan's step 18): the
//   warning in the portal's words, its buttons the portal's 44 pixels tall, the focus kept inside it, Escape staying
//   signed in; the end keeping a half-written message for the same patient. And someone a patient shares their record
//   with, writing on that patient's record, goes back to the same record when they sign in again, though signing in
//   starts every session on the account's own record, so what they typed is sent where they meant it.
// Type: Non-functional (usability)
// Requirements: NFR-12, proposed FR-63, FR-19
// Use case: UC 11, Sign in with multi-factor authentication; with UC 6 and UC 18 (the portal, proxies)
// Preconditions: Patients with confirmed portal accounts, signed in on the on-site app; for the proxy, Synthetic
//   Sharer has given Synthetic Helper's address access with messages, and Synthetic Helper is acting for them.
//   Chromium, 360 by 740 pixels, its clock installed, the server's cookie clock moved on with it.
// Inputs and system conditions: /portal/messages/new; a message typed; 13 minutes away; Tab, Shift+Tab; Escape; 15 minutes and 5
//   seconds away; "Sign in again" with the email and password; "Send message".
// Test data: Patients "Synthetic Portalwriter", "Synthetic Sharer", "Synthetic Helper". Message "Synthetic question for
//   the care team."
// Steps:
//   1. As Synthetic Portalwriter, type a message; let 13 minutes pass.
//   2. Press Tab, then Shift+Tab, then Escape.
//   3. Let 15 minutes and 5 seconds pass; sign in again; send.
//   4. As Synthetic Helper, acting for Synthetic Sharer, type a message; let 20 minutes pass; sign in again; send.
// Expected result: Step 1: the warning says "To keep your health information safe…", with the focus on "Stay signed
//   in", and every button in it at least 44 pixels tall within the 360-pixel screen. Step 2: Tab goes to "Sign out now"
//   and Shift+Tab back, inside the warning; Escape closes it, and the session has its 15 minutes again. Step 3: the page is cleared and comes
//   back with the message, which is saved in Synthetic Portalwriter's record. Step 4: the page comes back still saying
//   it is Synthetic Sharer's record, and the message is saved in Synthetic Sharer's record, not Synthetic Helper's.
// Creator: Tahseen Arnan
[Collection(BrowserTests.Collection)]
public class TC_UC11_12_Nfr12_PortalSessionEnd(WebApps apps) : IClassFixture<WebApps>
{
    const string Body = "Synthetic question for the care team.";

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    static readonly BrowserNewContextOptions Phone = new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = 360, Height = 740 } };

    async Task<long> MessagesAsync(Guid patient)
    {
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM ehr.messages WHERE patient_id = @patient AND body = @body", db);
        command.Parameters.AddWithValue("patient", patient);
        command.Parameters.AddWithValue("body", Body);
        return (long)(await command.ExecuteScalarAsync(Cancel))!;
    }

    static async Task WriteAsync(IPage page)
    {
        await page.FocusAsync("#message-subject");
        await page.Keyboard.TypeAsync("Synthetic subject");
        await page.FocusAsync("#message-body");
        await page.Keyboard.TypeAsync(Body);
    }

    [Fact]
    public async Task The_portals_warning_works_from_the_keyboard_on_a_phone_and_the_end_keeps_the_message()
    {
        var patient = await apps.InsertPatientAsync("Synthetic", "Portalwriter");
        var clinician = await apps.ClinicianAsync();
        await apps.AssignAsync(clinician, patient);
        var (email, password) = await apps.CreatePatientAccountAsync(patient);
        var jar = new CookieContainer();
        using (var signedIn = await PostFormAsync(Client(apps.OnPrem, jar), "/portal/sign-in", "/portal/sign-in", ("Input.Email", email), ("Input.Password", password)))
        {
            Assert.Equal("/portal", PathOf(signedIn));
        }

        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await ContextAsync(browser, new Session(jar, Client(apps.OnPrem, jar)), options: Phone);
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{server.Url}/portal/messages/new");
        await WriteAsync(page);

        // 1.
        await IdleAsync(apps, page, TimeSpan.FromMinutes(13));
        await page.WaitForSelectorAsync(OpenWarning);
        Assert.Contains("To keep your health information safe", await page.TextContentAsync("[data-session-idle-text]"));
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('[data-session-stay]')"));
        var buttons = await page.EvaluateAsync<string>($$"""
            () => [...document.querySelectorAll('{{Warning}} button')].filter(b => !b.hidden)
                .map(b => `${b.textContent.trim()} ${b.getBoundingClientRect().height} ${b.getBoundingClientRect().right}`).join('; ')
            """);
        Assert.True(await page.EvaluateAsync<bool>($$"""
            () => [...document.querySelectorAll('{{Warning}} button')].filter(b => !b.hidden)
                .every(b => b.getBoundingClientRect().height >= 44 && b.getBoundingClientRect().right <= 360)
            """), buttons);

        // 2. Tab and Shift+Tab move between the warning's buttons; the page behind it is inert, so nothing on it takes the
        // focus.
        await page.Keyboard.PressAsync("Tab");
        Assert.True(await page.EvaluateAsync<bool>($"document.querySelector('{Warning}').contains(document.activeElement)"));
        Assert.Equal("Sign out now", (await page.EvaluateAsync<string>("document.activeElement.textContent")).Trim());
        await page.Keyboard.PressAsync("Shift+Tab");
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('[data-session-stay]')"));
        await page.Keyboard.PressAsync("Escape");
        await page.WaitForSelectorAsync(OpenWarning, new() { State = WaitForSelectorState.Detached });
        Assert.InRange(await LeftAsync(page, "/portal/session"), 880, 900);

        // 3.
        await IdleAsync(apps, page, TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(5));
        await page.WaitForSelectorAsync("[data-session-why='idle']");
        Assert.Contains("to keep your health information safe", await SeenAsync(page));
        Assert.DoesNotContain(Body, await SeenAsync(page));
        await SignInAgainAsync(page, email, password);
        await page.WaitForSelectorAsync("#message-body");
        Assert.Equal(Body, await page.InputValueAsync("#message-body"));
        await page.ClickAsync("form.sc-form button[type=submit]");
        await page.WaitForURLAsync("**/portal/messages/*");
        Assert.Equal(1, await MessagesAsync(patient));
    }

    [Fact]
    public async Task A_proxy_goes_back_to_the_record_they_were_writing_on()
    {
        var sharer = await ProxyTests.PersonAsync(apps, "Synthetic", "Sharer");
        await apps.AssignAsync(await apps.ClinicianAsync(), sharer.Patient);
        var helper = await ProxyTests.PersonAsync(apps, "Synthetic", "Helper");
        await apps.AssignAsync(await apps.ClinicianAsync(), helper.Patient);
        await ProxyTests.GiveAsync(sharer, helper.Email, scope: "record-and-messages");
        var grant = await ProxyTests.GrantIdAsync(apps, sharer.Patient);
        await ProxyTests.ViewAsync(helper, grant);

        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await ContextAsync(browser, new Session(helper.Jar, helper.Client), options: Phone);
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{server.Url}/portal/messages/new");
        Assert.Contains("Sharer", await page.TextContentAsync("[data-acting]") ?? "");
        await WriteAsync(page);

        // 4.
        await IdleAsync(apps, page, TimeSpan.FromMinutes(20));
        await page.WaitForSelectorAsync("[data-session-why='idle']");
        await SignInAgainAsync(page, helper.Email, ProxyTests.Password);
        await page.WaitForSelectorAsync("#message-body");
        Assert.Equal(Body, await page.InputValueAsync("#message-body"));
        Assert.Contains("Sharer", await page.TextContentAsync("[data-acting]") ?? "");
        await page.ClickAsync("form.sc-form button[type=submit]");
        await page.WaitForURLAsync("**/portal/messages/*");
        Assert.Equal(1, await MessagesAsync(sharer.Patient));
        Assert.Equal(0, await MessagesAsync(helper.Patient));
    }
}
