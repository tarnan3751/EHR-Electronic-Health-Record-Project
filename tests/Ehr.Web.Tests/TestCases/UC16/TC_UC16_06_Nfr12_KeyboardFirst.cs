using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.AppointmentTests;

namespace Ehr.Web.Tests.TestCases.UC16;

// Test Case ID: TC-UC16-06
// Purpose / scenario: Booking an appointment is keyboard-first: from the chart to the booked appointment with no
//   mouse, in a real browser.
// Type: Non-functional (usability)
// Requirements: NFR-12
// Use case: UC 16, Schedule an appointment
// Preconditions: The clinician is signed in on the on-site app and the patient is on their panel, with no
//   appointments. Chromium, driven by Playwright, with the keyboard only.
// Inputs and system conditions: Key presses only: Tab, Enter and typing.
// Test data: Time 08:15. Reason "Synthetic: booked by keyboard".
// Steps:
//   1. Open the chart. Press Tab until "Book appointment" has the focus, and press Enter.
//   2. Press Tab to the time and type it; Tab to the reason and type it; press Enter.
// Expected result: Step 1 opens the form with the cursor in the clinician field, the signed-in clinician chosen and
//   tomorrow's date filled in. Step 2 books the appointment and returns to the chart, which lists it. Every focused
//   control shows the focus ring, and the page reports no script errors.
// Creator: Tahseen Arnan
//
// In the browser tests' collection, which doesn't run beside the others (AiReviewBrowserTests says why).
[Collection(BrowserTests.Collection)]
public class TC_UC16_06_Nfr12_KeyboardFirst(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Booking_an_appointment_works_from_the_keyboard()
    {
        var (_, patient, session) = await ClinicianAsync(apps, "Dr. Schedule Keys");
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await AiReviewBrowserTests.ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        const string focusRing = "getComputedStyle(document.activeElement).outlineStyle !== 'none'";

        await page.GotoAsync($"{server.Url}/patients/{patient}");
        await AiReviewBrowserTests.TabToAsync(page, "a[data-book-appointment]");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/appointments/new");
        await page.WaitForFunctionAsync("document.activeElement?.matches('#appointment-clinician') === true");
        Assert.True(await page.EvaluateAsync<bool>("document.querySelector('#appointment-day').value !== ''"));

        await AiReviewBrowserTests.TabToAsync(page, "#appointment-time");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        // The browser's time field takes the digits, and "AM" where its locale uses the 12-hour clock.
        await page.Keyboard.TypeAsync("0815AM");
        Assert.Equal("08:15", await page.InputValueAsync("#appointment-time"));
        await AiReviewBrowserTests.TabToAsync(page, "#appointment-reason");
        await page.Keyboard.TypeAsync("Synthetic: booked by keyboard");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == $"/patients/{patient}");
        Assert.Contains("Synthetic: booked by keyboard", await page.Locator("#chart-appointments ~ ul").TextContentAsync());

        Assert.Equal(["booked 08:15"], await RowsAsync(apps, patient, "status || ' ' || to_char(starts_at AT TIME ZONE 'UTC', 'HH24:MI')"));
        Assert.Empty(errors);
    }
}
