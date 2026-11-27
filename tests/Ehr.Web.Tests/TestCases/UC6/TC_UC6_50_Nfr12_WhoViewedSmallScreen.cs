using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RecordViewTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-50
// Purpose / scenario: Who viewed my record works on a phone and from the keyboard: at 360 pixels wide nothing scrolls
//   sideways, a long name, a long address and the emergency-access tag included, and the patient reaches "Older views" with Tab, sees
//   the focus ring on it, opens it with Enter, and reaches "Newer views" on the older page the same way. The notice
//   names its state in words, for a screen reader.
// Type: Non-functional (usability)
// Requirements: NFR-12, proposed FR-57
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a portal account, signed in. The audit log holds 60 views of the record by Dr. Test, a
//   minute apart, one by a clinician with a long name through emergency access, of the chart with its sensitive items
//   (the longest words a part has, step 13c2), and one by someone the record is shared with, whose address is 45
//   characters with nowhere to break. Chromium, driven by Playwright,
//   360 by 740 pixels, with the keyboard only.
// Inputs and system conditions: GET /portal/views; key presses only: Tab and Enter.
// Test data: The clinician "Dr. Bartholomew Featherstonehaugh-Wolfeschlegelsteinhausen"; the address, 32 hex digits
//   "@example.test".
// Steps:
//   1. Open Who viewed my record.
//   2. Press Tab until "Older views" has the focus, and press Enter.
//   3. On the older page, press Tab until "Newer views" has the focus.
// Expected result: Step 1: the page is no wider than the window, the address wrapping inside its line; the notice reads
//   "Emergency access.". Step 2: the
//   focused link shows the focus ring; Enter opens the older page, which is no wider than the window either. Step 3:
//   the focused link shows the focus ring. No script errors.
// Creator: Tahseen Arnan
//
// In the browser tests' collection, which doesn't run beside the others (AiReviewBrowserTests says why).
[Collection(BrowserTests.Collection)]
public class TC_UC6_50_Nfr12_WhoViewedSmallScreen(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task At_360_pixels_nothing_scrolls_sideways_and_the_pages_open_from_the_keyboard()
    {
        var (clinician, _, patient) = await ViewedPatientAsync(apps, "Phoned");
        await ReadsAsync(apps, patient.Patient, clinician.Id, 60);
        var away = await apps.CreateStaffAsync("Dr. Bartholomew Featherstonehaugh-Wolfeschlegelsteinhausen", StaffRoles.Clinician);
        await using (var connection = await apps.OpenDatabaseAsync())
        {
            await using var window = new Npgsql.NpgsqlCommand(
                "INSERT INTO ehr.break_glass_accesses (id, patient_id, clinician_id, reason, granted_at, expires_at) VALUES (gen_random_uuid(), @patient, @clinician, 'Synthetic reason for a test.', now() - interval '1 hour', now() + interval '3 hours')",
                connection);
            window.Parameters.AddWithValue("patient", patient.Patient);
            window.Parameters.AddWithValue("clinician", away.Id);
            await window.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await PartReadsAsync(apps, patient.Patient, away.Id, 1, (AuditActions.ReadSensitive, "patients", AuditParts.Chart));
        // Someone the record is shared with, named by an address of 45 characters with nowhere to break.
        var proxy = await ProxyTests.PersonAsync(apps, "Synthetic", "Longaddress");
        await ReadsAsync(apps, patient.Patient, proxy.Account, 1, newestMinutesAgo: 0);

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

        // Step 1.
        await page.GotoAsync($"{server.Url}{ViewsPath}");
        Assert.True(await page.EvaluateAsync<bool>(fits), "The page scrolls sideways at 360 pixels.");
        Assert.Contains("Emergency access.", await page.Locator("[data-emergency=explained]").TextContentAsync());
        Assert.Contains("Featherstonehaugh", await page.Locator("main").TextContentAsync());
        Assert.Contains("Chart, with sensitive items", await page.Locator("main").TextContentAsync());
        Assert.Contains(proxy.Email, await page.Locator("main").TextContentAsync());

        // Step 2.
        await AiReviewBrowserTests.TabToAsync(page, "a[href^='/portal/views?older=']");
        Assert.Equal("Older views", await page.EvaluateAsync<string>("document.activeElement.textContent"));
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync(url => url.Contains("/portal/views?older=", StringComparison.Ordinal));
        await page.WaitForFunctionAsync("() => document.readyState === 'complete' && document.querySelector(\"a[href^='/portal/views?newer=']\") !== null");
        Assert.True(await page.EvaluateAsync<bool>(fits), "The older page scrolls sideways at 360 pixels.");

        // Step 3.
        await AiReviewBrowserTests.TabToAsync(page, "a[href^='/portal/views?newer=']");
        await page.WaitForFunctionAsync("() => document.activeElement && document.activeElement.textContent === 'Newer views'");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        Assert.Empty(errors);
    }
}
