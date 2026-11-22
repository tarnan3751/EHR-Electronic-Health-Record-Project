using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RefillRequestTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC21;

// Test Case ID: TC-UC21-06
// Purpose / scenario: Answering refill requests works from the keyboard alone: from the inbox to the request, whose page
//   opens on "Approve: write the order"; to the order form, which opens on its item, filled in; through the details to
//   "Sign order"; and back to the inbox for the next request, declined with a message, after a message of spaces alone was
//   refused and the cursor came back in the message. Every control reached shows the focus ring.
// Type: Non-functional (usability)
// Requirements: NFR-12, proposed FR-58
// Use case: UC 21, Answer a refill request
// Preconditions: Dr. Test has the patient on their panel. The patient has asked to renew lisinopril, then amlodipine.
//   Chromium, driven by Playwright, signed in as Dr. Test.
// Inputs and system conditions: GET /inbox; key presses only: Tab, letters and Enter.
// Test data: dose "10 mg", route "By mouth", frequency "Once a day"; the message "Synthetic: please book a visit.".
// Steps:
//   1. In the inbox, press Tab until "Review the request" has the focus, and press Enter.
//   2. Press Enter on "Approve: write the order"; Tab to the dose, route and frequency, typing each; Tab to "Sign order"
//      and press Enter.
//   3. Back in the inbox, Tab to "Review the request" and press Enter; Tab to the message, type three spaces, Tab to
//      "Decline request" and press Enter; type the message, Tab to "Decline request" and press Enter.
// Expected result: Step 1: the request's page opens with the focus on "Approve: write the order". Step 2: the order form
//   opens with the item "lisinopril 10 MG Oral Tablet", fixed, and the focus in the dose; signing lands on the request's
//   page, "Approved:". Step 3: the spaces come back refused with the focus in the message, not on "Approve"; then the
//   page says "Declined:". Both requests are answered; no script errors.
// Creator: Tahseen Arnan
//
// In the browser tests' collection, which doesn't run beside the others (AiReviewBrowserTests says why).
[Collection(BrowserTests.Collection)]
public class TC_UC21_06_Nfr12_KeyboardFirst(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Answering_refill_requests_works_from_the_keyboard()
    {
        var (_, session, patient) = await PortalPatientAsync(apps, "Keyboard");
        await MedicationAsync(apps, patient.Patient);
        await MedicationAsync(apps, patient.Patient, "amlodipine 5 MG Oral Tablet", "197361");
        using (var asked = await AskAsync(patient.Client, Lisinopril))
        {
            Assert.Equal(PortalPath, PathOf(asked));
        }

        using (var asked = await AskAsync(patient.Client, "amlodipine 5 MG Oral Tablet"))
        {
            Assert.Equal(PortalPath, PathOf(asked));
        }

        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await AiReviewBrowserTests.ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        const string focusRing = "getComputedStyle(document.activeElement).outlineStyle !== 'none'";

        await page.GotoAsync($"{server.Url}/inbox");
        await AiReviewBrowserTests.TabToAsync(page, "a[data-inbox-source]");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/refills/*");
        await page.WaitForFunctionAsync("document.activeElement.matches('[data-refill-approve]')");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");

        await page.WaitForURLAsync(url => url.Contains("refill=", StringComparison.Ordinal));
        await page.WaitForFunctionAsync("document.activeElement.matches('#order-dose')");
        Assert.Equal("lisinopril 10 MG Oral Tablet", await page.EvaluateAsync<string>("document.querySelector('#order-item').value"));
        Assert.True(await page.EvaluateAsync<bool>("document.querySelector('#order-item').readOnly"));
        foreach (var (field, value) in new[] { ("#order-dose", "10 mg"), ("#order-route", "By mouth"), ("#order-frequency", "Once a day") })
        {
            await AiReviewBrowserTests.TabToAsync(page, field);
            Assert.True(await page.EvaluateAsync<bool>(focusRing));
            await page.Keyboard.TypeAsync(value);
        }

        await AiReviewBrowserTests.TabToAsync(page, "[data-order-sign]");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync(url => url.Contains("answered=approved", StringComparison.Ordinal));
        Assert.Contains("Approved:", await page.Locator("[data-refill-answered]").TextContentAsync());

        await page.GotoAsync($"{server.Url}/inbox");
        await AiReviewBrowserTests.TabToAsync(page, "a[data-inbox-source]");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/refills/*");
        await page.WaitForFunctionAsync("document.activeElement.matches('[data-refill-approve]')");
        await AiReviewBrowserTests.TabToAsync(page, "#refill-message");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        // Spaces alone pass the browser's check and not the server's: the page comes back with the cursor in the message.
        await page.Keyboard.TypeAsync("   ");
        await AiReviewBrowserTests.TabToAsync(page, "[data-refill-decline] button[type=submit]");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForSelectorAsync(".sc-field__error");
        await page.WaitForFunctionAsync("document.activeElement.matches('#refill-message')");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.TypeAsync("Synthetic: please book a visit.");
        await AiReviewBrowserTests.TabToAsync(page, "[data-refill-decline] button[type=submit]");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync(url => url.Contains("answered=declined", StringComparison.Ordinal));
        Assert.Contains("Declined:", await page.Locator("[data-refill-answered]").TextContentAsync());

        var requests = await RequestsAsync(apps, patient.Patient);
        Assert.StartsWith("completed|", requests[0], StringComparison.Ordinal);
        Assert.StartsWith("rejected|", requests[1], StringComparison.Ordinal);
        Assert.Empty(errors);
    }
}
