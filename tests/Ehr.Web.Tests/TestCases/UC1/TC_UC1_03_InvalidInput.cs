using System.Net;
using Xunit;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC1;

// Test Case ID: TC-UC1-03
// Purpose / scenario: The clinician signs an order with a required field left empty. The field is highlighted with
//   its message, nothing is saved, and what was entered stays in the form.
// Type: Functional (invalid input)
// Requirements: FR-05
// Use case: UC 1, Place an order
// Preconditions: The clinician is signed in on the on-site app and the patient is on their panel. The patient has no
//   orders. The clinic's server is up.
// Inputs and system conditions: POST /patients/{id}/orders/new?type=lab as js/orders.js sends it, on the on-site app.
// Test data: Patient Synthetic Orderable. Item "Complete blood count (CBC)", Reason for the test " " (one space, so
//   empty), Notes for the lab "Fasting".
// Steps:
//   1. Open the lab order form.
//   2. Enter the item and the notes, leave the reason empty, and select "Sign order".
// Expected result: The answer is 422 with the form again: the Reason for the test field has aria-invalid="true" and
//   the message "Reason for the test is required."; the item still reads "Complete blood count (CBC)" and the notes
//   "Fasting". No order is saved.
// Creator: Tahseen Arnan
public class TC_UC1_03_InvalidInput(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_empty_required_field_is_highlighted_and_nothing_is_saved()
    {
        var (_, patient, session) = await OnPanelAsync(apps);

        using var response = await SignAsync(session.Client, patient, "lab", "Complete blood count (CBC)", [("indication", " "), ("notes", "Fasting")]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("id=\"order-indication\"", html);
        Assert.Matches("id=\"order-indication\"[^>]* aria-invalid=\"true\"", html);
        Assert.Contains("<p class=\"sc-field__error\" id=\"order-indication-error\">Reason for the test is required.</p>", html);
        Assert.Contains("value=\"Complete blood count (CBC)\"", html);
        Assert.Contains("value=\"Fasting\"", html);
        Assert.Equal(0, await OrdersAsync(apps, patient));
    }
}
