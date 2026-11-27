using System.Net;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC1;

// Test Case ID: TC-UC1-08
// Purpose / scenario: A clinician changes an active order. The signed order is not edited: a new order is signed and
//   the old one is discontinued as replaced, each naming the other. The order being replaced doesn't count as a
//   duplicate of its replacement, but another active order for the same item still does.
// Type: Functional (alternative flow)
// Requirements: FR-05, proposed FR-37
// Use case: UC 1, Place an order (changing a signed order)
// Preconditions: The clinician is signed in on the on-site app and the patient is on their panel. The patient has an
//   active Metformin order, signed in step 1. The clinic's server is up.
// Inputs and system conditions: GET and POST /patients/{id}/orders/new?type=medication&replaces={order} as
//   js/orders.js sends it, on the on-site app.
// Test data: Patient Synthetic Orderable. "Metformin 500 mg tablet" (SYN-MED-002), Dose "500 mg", Route "By mouth",
//   Frequency "Twice a day"; changed to Dose "1000 mg" with Reason for the change "Synthetic: A1c above goal". Then
//   the item changed to "Lisinopril 10 mg tablet" (SYN-MED-001), which the patient already has an active order for.
// Steps:
//   1. Sign the Metformin order and follow its "Change" link on the chart.
//   2. Change the dose, give the reason and choose "Sign changed order".
//   3. Open the address the answer gives (the chart), then "Discontinued orders".
//   4. Sign a Lisinopril order; then change the new Metformin order's item to Lisinopril and sign, with no reason for
//      the duplicate order; then sign again with one.
// Expected result: The change form is titled "Change order", shows "Order being changed." with the old order, opens filled with
//   its item and details, offers no choice of type, and its button says "Sign changed order". Step 2 is answered 200
//   with no duplicate warning. The chart says "Order changed." and lists one active Metformin order, dose 1000 mg. The old order is revoked with
//   the reason and names the new order; the new order names the old one; the discontinued list shows the old order
//   with a "Changed" tag. In step 4 the first attempt is answered 422 with the duplicate warning and nothing changes;
//   the second saves, and the Metformin order is discontinued with the default reason "Replaced by a changed order".
// Creator: Tahseen Arnan
public class TC_UC1_08_ChangeAlternativeFlow(WebApps apps) : IClassFixture<WebApps>
{
    static (string, string)[] Metformin(string dose) => [("dose", dose), ("route", "By mouth"), ("frequency", "Twice a day")];

    [Fact]
    public async Task Changing_an_order_signs_a_new_one_and_discontinues_the_old_one_as_replaced()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        var old = await SignedAsync(session.Client, patient, "medication", "Metformin 500 mg tablet", Metformin("500 mg"));

        var form = await session.Client.GetStringAsync(ChangePath(patient, "medication", old), Cancel);
        Assert.Contains("<h1>Change order</h1>", form);
        Assert.Contains("data-order-replaces", form);
        Assert.Contains("Order being changed. Metformin 500 mg tablet Dose: 500 mg; Route: By mouth; Frequency: Twice a day", Words(form));
        Assert.Contains("name=\"Item\" value=\"Metformin 500 mg tablet\"", form);
        Assert.Contains("name=\"Details[dose]\" value=\"500 mg\"", form);
        Assert.Contains("name=\"ChangeReason\"", form);
        Assert.Contains(">Sign changed order</button>", form);
        Assert.DoesNotContain("aria-label=\"Type of order\"", form);

        using var changed = await ChangeAsync(session.Client, patient, "medication", old, "Metformin 500 mg tablet", Metformin("1000 mg"), "Synthetic: A1c above goal");
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var url = System.Text.Json.JsonDocument.Parse(await changed.Content.ReadAsStringAsync(Cancel)).RootElement.GetProperty("url").GetString()!;
        var replacement = Guid.Parse(System.Web.HttpUtility.ParseQueryString(new Uri(Base, url).Query)["ordered"]!);

        var chart = await session.Client.GetStringAsync(url, Cancel);
        Assert.Contains("Order changed.", Words(chart));
        Assert.Contains("Dose: 1000 mg", chart);
        Assert.DoesNotContain("Dose: 500 mg", chart);
        Assert.Equal(1, await ActiveAsync(apps, patient));
        Assert.Equal($"revoked|Synthetic: A1c above goal|{clinician.Id}|-|{replacement}", await RowAsync(apps, old));
        Assert.Equal($"active|-|-|{old}|-", await RowAsync(apps, replacement));

        var listed = await session.Client.GetStringAsync($"/patients/{patient}/orders/discontinued", Cancel);
        Assert.Contains("Dose: 500 mg", listed);
        Assert.Contains(">Changed</span>", listed);
        Assert.Contains("Synthetic: A1c above goal", listed);

        // Another active order for the item the change picks is still a duplicate.
        await SignedAsync(session.Client, patient, "medication", "Lisinopril 10 mg tablet", Metformin("10 mg"));
        using var warned = await ChangeAsync(session.Client, patient, "medication", replacement, "Lisinopril 10 mg tablet", Metformin("10 mg"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, warned.StatusCode);
        var warning = await warned.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("data-order-duplicate", warning);
        Assert.Contains("data-order-replaces", warning);
        Assert.Equal($"active|-|-|{old}|-", await RowAsync(apps, replacement));
        Assert.Equal(2, await ActiveAsync(apps, patient));

        using var confirmed = await ChangeAsync(session.Client, patient, "medication", replacement, "Lisinopril 10 mg tablet", Metformin("10 mg"),
            duplicateReason: "Synthetic: second prescriber");
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.StartsWith($"revoked|{Ehr.Domain.Orders.ClinicalOrders.ReplacedReason}|{clinician.Id}|{old}|", await RowAsync(apps, replacement));
        Assert.Equal(2, await OrdersAsync(apps, patient, "SYN-MED-001"));
        Assert.Equal(2, await ActiveAsync(apps, patient));
    }
}
