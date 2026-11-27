using System.Net;
using Xunit;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC1;

// Test Case ID: TC-UC1-09
// Purpose / scenario: Discontinuing an order without a reason, or changing one with a required field left empty, is
//   refused: the field is highlighted with what to fix, nothing is saved, and what was entered stays.
// Type: Functional (invalid input)
// Requirements: FR-05, proposed FR-37
// Use case: UC 1, Place an order (changing a signed order)
// Preconditions: The clinician is signed in on the on-site app and the patient is on their panel. The patient has an
//   active Metformin order. The clinic's server is up.
// Inputs and system conditions: POST /patients/{id}/orders/{order}/discontinue and POST
//   /patients/{id}/orders/new?type=medication&replaces={order}, as js/orders.js sends them, on the on-site app.
// Test data: "Metformin 500 mg tablet", Dose "500 mg", Route "By mouth", Frequency "Twice a day". Reasons: none, three
//   spaces, and 501 letters. The change: Dose empty, Route "By mouth", Frequency "Once a day"; and a Reason for the
//   change of 501 letters.
// Steps:
//   1. Choose "Discontinue order" with no reason, then with only spaces, then with 501 letters.
//   2. Sign a change with the dose empty.
//   3. Sign a change with a reason of 501 letters.
// Expected result: Each is answered 422. Discontinuing: "Reason for discontinuing is required." (or "Reason must be
//   500 characters or less."), with the reason field marked invalid and described by the message.
//   Changing: "Dose is required." with the dose marked invalid and "Once a day" still in its field; then "Reason must
//   be 500 characters or less." The order is still active and unchanged, and it is still the patient's only
//   order.
// Creator: Tahseen Arnan
public class TC_UC1_09_ChangeInvalidInput(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_missing_reason_or_field_is_highlighted_and_nothing_is_saved()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        var order = await SignedAsync(session.Client, patient, "medication", "Metformin 500 mg tablet",
            [("dose", "500 mg"), ("route", "By mouth"), ("frequency", "Twice a day")]);
        var tooLong = new string('a', 501);

        foreach (var (reason, message) in new[]
        {
            ((string?)null, "Reason for discontinuing is required."),
            ("   ", "Reason for discontinuing is required."),
            (tooLong, "Reason must be 500 characters or less."),
        })
        {
            using var refused = await DiscontinueAsync(session.Client, patient, order, reason);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            var html = await refused.Content.ReadAsStringAsync(Cancel);
            Assert.Contains($"<p class=\"sc-field__error\" id=\"discontinue-reason-error\">{message}</p>", html);
            Assert.Matches("<textarea [^>]*name=\"Reason\"[^>]*aria-invalid=\"true\" aria-describedby=\"discontinue-reason-error\"", html);
        }

        using var noDose = await ChangeAsync(session.Client, patient, "medication", order, "Metformin 500 mg tablet",
            [("dose", ""), ("route", "By mouth"), ("frequency", "Once a day")]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noDose.StatusCode);
        var form = await noDose.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("<p class=\"sc-field__error\" id=\"order-dose-error\">Dose is required.</p>", form);
        Assert.Contains("name=\"Details[frequency]\" value=\"Once a day\"", form);
        Assert.Contains("data-order-replaces", form);

        using var longReason = await ChangeAsync(session.Client, patient, "medication", order, "Metformin 500 mg tablet",
            [("dose", "850 mg"), ("route", "By mouth"), ("frequency", "Once a day")], changeReason: tooLong);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, longReason.StatusCode);
        Assert.Contains("<p class=\"sc-field__error\" id=\"order-change-reason-error\">Reason must be 500 characters or less.</p>",
            await longReason.Content.ReadAsStringAsync(Cancel));

        Assert.Equal("active|-|-|-|-", await RowAsync(apps, order));
        Assert.Equal(1, await OrdersAsync(apps, patient));
    }
}
