using Ehr.Data.Accounts;
using Microsoft.Playwright;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.SessionBrowser;

namespace Ehr.Web.Tests.TestCases.UC11;

// Test Case ID: TC-UC11-11
// Purpose / scenario: A session ends without the warning being seen, and nothing typed is lost to it (the expansion
//   plan's step 18, and Tahseen's ruling that a sign-out never loses typed text). A laptop asleep past the end clears
//   its page on waking, and someone else signing in on it clears what was kept and goes to their own home page.
//   Signing out in another tab clears this one, keeping what was typed for the same account. Signing out on purpose
//   with something typed keeps it the same way. A note's draft is on the server whatever happens, saved as it was
//   typed. And off-site, with the clinic's server cut off, the warning and the end work from the cloud app alone.
// Type: Functional (exceptional condition)
// Requirements: proposed FR-63, FR-28, FR-34
// Use case: UC 11, Sign in with multi-factor authentication
// Preconditions: Clinicians with a patient on their panel, signed in. Chromium, its clock installed, the server's
//   cookie clock moved on with it.
// Inputs and system conditions: 20 minutes with the browser's timers stopped, then waking; another clinician signing
//   in in the page's frame; a second tab's "Sign out"; "Sign out" with a message typed; a note typed and saved, then 15
//   minutes; the cloud app cut off from the clinic's server, 13 then 15 minutes.
// Test data: Patients "Synthetic Asleep", "Synthetic Elsewhere", "Synthetic Notesaved", "Synthetic Cutoff".
// Steps:
//   1. Type a message; let 20 minutes pass at once; then Dr. Other signs in in the frame.
//   2. Type a message; sign out in a second tab; sign in again in the first.
//   3. Type a message; sign out on purpose; sign in again.
//   4. Type in a note's Subjective; let it save; let 15 minutes pass; sign in again.
//   5. Off-site, cut off: let 13 minutes pass, stay signed in; let 15 minutes pass.
// Expected result: Step 1: the page is cleared with "Signed out. Nothing was done here for 15 minutes…", no warning seen
//   first; after Dr. Other signs in, the browser is on Dr. Other's patients, and the message is nowhere. Step 2: the
//   first tab says "Signed out. This sign-in has ended…", keeps the message, and has it back after signing in. Step 3:
//   "Signed out. You signed out.", the message kept and back after signing in. Step 4: the draft on the server holds
//   what was typed; after signing in the editor has it. Step 5: the warning opens and closes, and the page is cleared,
//   with nothing forwarded to the clinic's server.
// Creator: Tahseen Arnan
[Collection(BrowserTests.Collection)]
public class TC_UC11_11_EndWithoutWarning(WebApps apps) : IClassFixture<WebApps>
{
    const string Body = "Synthetic words still to send.";

    async Task<(StaffAccount Clinician, Guid Patient, IPage Page, IAsyncDisposable Cleanup)> OpenAsync(
        string name, string family, string path, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? app = null)
    {
        var clinician = await apps.CreateStaffAsync(name, StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Synthetic", family);
        await apps.AssignAsync(clinician, patient);
        // Signing in is forwarded to the clinic's server, so the cut-off app's session is signed in through the cloud app.
        var session = await SignInAsync(app == apps.CloudCutOff ? apps.Cloud : app ?? apps.OnPrem, clinician);
        var server = await ClinicalBrowserHost.StartAsync(apps, app);
        var playwright = await Playwright.CreateAsync();
        var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        var context = await ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        await page.GotoAsync(server.Url + path.Replace("{patient}", patient.ToString(), StringComparison.Ordinal));
        return (clinician, patient, page, new Cleanup(server, playwright, browser, context));
    }

    sealed class Cleanup(ClinicalBrowserHost server, IPlaywright playwright, IBrowser browser, IBrowserContext context) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await context.DisposeAsync();
            await browser.DisposeAsync();
            playwright.Dispose();
            await server.DisposeAsync();
        }
    }

    static async Task TypeMessageAsync(IPage page)
    {
        await page.FocusAsync("#message-body");
        await page.Keyboard.TypeAsync(Body);
    }

    [Fact]
    public async Task Asleep_past_the_end_the_page_clears_on_waking_and_someone_else_signing_in_clears_what_was_kept()
    {
        var (_, _, page, cleanup) = await OpenAsync("Dr. Asleep", "Asleep", "/patients/{patient}/messages/new");
        await using var _ = cleanup;
        await WatchWarningAsync(page);
        await TypeMessageAsync(page);

        // 1. The browser's timers stopped while asleep: its clock jumps 20 minutes in one go.
        await IdleAsync(apps, page, TimeSpan.FromMinutes(20));
        await page.WaitForSelectorAsync("[data-session-why='idle']");
        Assert.False(await WarnedAsync(page));
        Assert.DoesNotContain(Body, await SeenAsync(page));

        var other = await apps.CreateStaffAsync("Dr. Other", StaffRoles.Clinician);
        await SignInAgainAsync(page, other);
        await page.WaitForURLAsync("**/patients");
        Assert.DoesNotContain(Body, await SeenAsync(page));
        Assert.DoesNotContain("Asleep", await SeenAsync(page));
    }

    [Fact]
    public async Task Signed_out_in_another_tab_this_one_clears_and_keeps_what_was_typed()
    {
        var (clinician, _, page, cleanup) = await OpenAsync("Dr. Elsewhere", "Elsewhere", "/patients/{patient}/messages/new");
        await using var _ = cleanup;
        await TypeMessageAsync(page);

        // 2.
        var second = await page.Context.NewPageAsync();
        await second.GotoAsync(page.Url.Replace("/messages/new", "", StringComparison.Ordinal));
        await second.ClickAsync(".sc-appbar__signout");
        await page.WaitForSelectorAsync("[data-session-why='ended']");
        Assert.True(await page.IsVisibleAsync("[data-session-kept]"));
        Assert.DoesNotContain(Body, await SeenAsync(page));

        await SignInAgainAsync(page, clinician);
        await page.WaitForSelectorAsync("#message-body");
        Assert.Equal(Body, await page.InputValueAsync("#message-body"));
    }

    [Fact]
    public async Task Signing_out_on_purpose_with_something_typed_keeps_it()
    {
        var (clinician, _, page, cleanup) = await OpenAsync("Dr. On Purpose", "Onpurpose", "/patients/{patient}/messages/new");
        await using var _ = cleanup;
        await TypeMessageAsync(page);

        // 3.
        await page.ClickAsync(".sc-appbar__signout");
        await page.WaitForSelectorAsync("[data-session-why='signed-out']");
        Assert.Contains("Signed out. You signed out.", await SeenAsync(page));
        Assert.DoesNotContain(Body, await SeenAsync(page));
        Assert.True(await EndedAsync(page));

        await SignInAgainAsync(page, clinician);
        await page.WaitForSelectorAsync("#message-body");
        Assert.Equal(Body, await page.InputValueAsync("#message-body"));
    }

    [Fact]
    public async Task A_notes_draft_is_saved_as_typed_whatever_the_session_does()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Note Saved", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Synthetic", "Notesaved");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(apps.OnPrem, clinician);
        using var created = await PostFormAsync(session.Client, $"/patients/{patient}?handler=NewNote", $"/patients/{patient}");
        var editor = PathOf(created);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        await page.GotoAsync(server.Url + editor);

        // 4.
        await page.FocusAsync("textarea[name=Subjective]");
        await page.Keyboard.TypeAsync("Synthetic subjective typed before the break.");
        await page.Clock.RunForAsync(1000);
        await page.WaitForFunctionAsync("document.getElementById('note-save-status').textContent === 'Saved'");
        await IdleAsync(apps, page, TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(5));
        await page.WaitForSelectorAsync("[data-session-why='idle']");

        await using (var db = await apps.OpenDatabaseAsync())
        {
            await using var command = new NpgsqlCommand("SELECT subjective FROM ehr.clinical_drafts WHERE patient_id = @patient", db);
            command.Parameters.AddWithValue("patient", patient);
            Assert.Equal("Synthetic subjective typed before the break.", (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        await SignInAgainAsync(page, clinician);
        await page.WaitForSelectorAsync("textarea[name=Subjective]");
        Assert.Equal("Synthetic subjective typed before the break.", await page.InputValueAsync("textarea[name=Subjective]"));
    }

    [Fact]
    public async Task Off_site_with_the_clinics_server_cut_off_the_warning_and_the_end_work_from_the_cloud_app()
    {
        var (_, _, page, cleanup) = await OpenAsync("Dr. Cut Off", "Cutoff", "/quick-texts", apps.CloudCutOff);
        await using var _ = cleanup;

        // 5.
        await IdleAsync(apps, page, TimeSpan.FromMinutes(13));
        await page.WaitForSelectorAsync(OpenWarning);
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForSelectorAsync(OpenWarning, new() { State = WaitForSelectorState.Detached });
        Assert.InRange(await LeftAsync(page), 880, 900);

        await IdleAsync(apps, page, TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(5));
        await page.WaitForSelectorAsync("[data-session-why='idle']");
    }
}
