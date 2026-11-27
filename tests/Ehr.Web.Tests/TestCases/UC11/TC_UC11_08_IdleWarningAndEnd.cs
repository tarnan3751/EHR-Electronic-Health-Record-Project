using Ehr.Data.Accounts;
using Microsoft.Playwright;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.SessionBrowser;

namespace Ehr.Web.Tests.TestCases.UC11;

// Test Case ID: TC-UC11-08
// Purpose / scenario: A clinician leaves a message half written and walks away (the expansion plan's step 18). Two
//   minutes before the 15 minutes are up, a warning takes the focus; "Stay signed in" keeps the session without
//   reloading the page, so what was typed and where the page was scrolled stay as they were. Left again, the session
//   ends: the page is taken off the screen, title and all, and what was typed is kept in the tab, out of sight. Signing
//   in again, in the frame the page offers, brings the page back as it was, and the message sends.
// Type: Functional (successful flow)
// Requirements: proposed FR-63, FR-28, NFR-12
// Use case: UC 11, Sign in with multi-factor authentication
// Preconditions: Dr. Idle Writer has the patient on their panel and is signed in. Chromium, driven by Playwright, its
//   clock installed; the server's cookie clock is moved on with it (ShiftedClock). The window is 640 pixels tall, so
//   the form scrolls.
// Inputs and system conditions: /patients/{id}/messages/new; a subject and a message typed; 13 minutes away; Enter;
//   15 minutes and 5 seconds away; "Sign in again" with the password and code, from the keyboard; "Send message".
// Test data: Patient "Synthetic Idlemessage". Subject "Synthetic follow-up", message "Synthetic text typed before the
//   break."
// Steps:
//   1. Open the new-message page, type the subject and the message, scroll to the bottom.
//   2. Let 13 minutes pass.
//   3. Press Enter.
//   4. Let 15 minutes and 5 seconds pass.
//   5. Select "Sign in again" and sign in from the keyboard.
//   6. Send the message.
// Expected result: Step 2: the warning is open, "You'll be signed out soon", with the focus on "Stay signed in", the
//   countdown under 2 minutes, and the sentence that what was typed stays. Step 3: it closes; the page wasn't
//   reloaded, the typed text and the scroll position are as they were, and the server gives the session 15 minutes
//   again. Step 4: the page says "Signed out. No activity for 15 minutes." and that what was typed comes back;
//   neither the patient's name, in the page or its title, nor the typed text is on the page. Step 5: the page comes back
//   as it was, the text in its fields. Step 6: the message is saved, as typed.
// Creator: Tahseen Arnan
[Collection(BrowserTests.Collection)]
public class TC_UC11_08_IdleWarningAndEnd(WebApps apps) : IClassFixture<WebApps>
{
    const string Subject = "Synthetic follow-up";
    const string Body = "Synthetic text typed before the break.";

    [Fact]
    public async Task The_warning_keeps_the_page_and_the_end_keeps_what_was_typed_for_the_same_account()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Idle Writer", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Synthetic", "Idlemessage");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(apps.OnPrem, clinician);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await ContextAsync(browser, session,
            options: new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = 1280, Height = 640 } });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);

        // 1.
        await page.GotoAsync($"{server.Url}/patients/{patient}/messages/new");
        await page.FocusAsync("#message-subject");
        await page.Keyboard.TypeAsync(Subject);
        await page.FocusAsync("#message-body");
        await page.Keyboard.TypeAsync(Body);
        await page.EvaluateAsync("window.__notReloaded = true; window.scrollTo(0, document.body.scrollHeight)");
        var scrolled = await page.EvaluateAsync<double>("window.scrollY");
        Assert.True(scrolled > 0);

        // 2.
        await IdleAsync(apps, page, TimeSpan.FromMinutes(13));
        await page.WaitForSelectorAsync(OpenWarning);
        Assert.Equal("You'll be signed out soon", await page.TextContentAsync($"{Warning} h2"));
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('[data-session-stay]')"));
        Assert.True(await page.IsVisibleAsync("[data-session-typed]"));
        Assert.Matches("^[01]:[0-5][0-9]$", await page.TextContentAsync("[data-session-idle-text] [data-session-countdown]"));

        // 3.
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForSelectorAsync(OpenWarning, new() { State = WaitForSelectorState.Detached });
        Assert.True(await page.EvaluateAsync<bool>("window.__notReloaded === true"));
        Assert.Equal(Subject, await page.InputValueAsync("#message-subject"));
        Assert.Equal(Body, await page.InputValueAsync("#message-body"));
        Assert.Equal(scrolled, await page.EvaluateAsync<double>("window.scrollY"));
        Assert.InRange(await LeftAsync(page), 890, 900);

        // 4.
        await IdleAsync(apps, page, TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(5));
        await page.WaitForSelectorAsync("[data-session-why='idle']");
        var seen = await SeenAsync(page);
        Assert.Contains("Signed out. No activity for 15 minutes.", seen);
        Assert.Contains("Sign in again in this tab to get back what you typed.", seen);
        Assert.DoesNotContain("Idlemessage", seen);
        Assert.DoesNotContain(Body, seen);
        Assert.Equal("Signed out", await page.TitleAsync());

        // 5.
        await SignInAgainAsync(page, clinician);
        await page.WaitForSelectorAsync("#message-body");
        Assert.True(await page.EvaluateAsync<bool>("window.__notReloaded === true"));
        Assert.Equal(Body, await page.InputValueAsync("#message-body"));
        Assert.Contains("Idlemessage", await page.TitleAsync());

        // 6.
        await page.ClickAsync("button[type=submit].sc-btn--primary");
        await page.WaitForURLAsync($"**/patients/{patient}/messages/*");
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT m.body FROM ehr.messages m WHERE m.patient_id = @patient", db);
        command.Parameters.AddWithValue("patient", patient);
        Assert.Equal(Body, (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Empty(errors);
    }
}
