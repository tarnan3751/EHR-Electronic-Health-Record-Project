using System.Net;
using Xunit;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC1;

// Test Case ID: TC-UC1-02
// Purpose / scenario: The clinician orders an item the patient already has an active order for. The form warns and
//   saves nothing until a reason is given; with a reason, the order is saved. Two submissions at once save one order.
// Type: Functional (alternative flow)
// Requirements: FR-05
// Use case: UC 1, Place an order
// Preconditions: The clinician is signed in on the on-site app and the patient is on their panel. The patient has one
//   active order for the item, signed in step 1. The clinic's server is up.
// Inputs and system conditions: POST /patients/{id}/orders/new?type=lab as js/orders.js sends it, on the on-site app,
//   three times in turn, then five times at once for another item.
// Test data: Patient Synthetic Orderable. Item "Complete blood count (CBC)" (SYN-LAB-001), Reason for the test
//   "Synthetic fatigue". Reason for duplicate order "Synthetic repeat: sample clotted". For the five at once:
//   "Lipid panel" (SYN-LAB-004).
// Steps:
//   1. Sign a Complete blood count (CBC) order.
//   2. Sign the same order again, with no reason.
//   3. Sign it again with the reason.
//   4. Send five Lipid panel orders at the same moment.
// Expected result: Step 2 is answered 422 with the warning "An active order for Complete blood count (CBC) was signed
//   ... UTC." and a field "Reason for duplicate order", named DuplicateReason, the entries
//   still filled in, and still one order saved. Step 3 saves a second order for SYN-LAB-001. Of the five at once,
//   exactly one is saved and the others get the warning.
// Creator: Tahseen Arnan
public class TC_UC1_02_AlternativeFlow(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_duplicate_active_order_warns_and_needs_a_reason()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        using var first = await SignAsync(session.Client, patient, "lab", "Complete blood count (CBC)", Cbc);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var warned = await SignAsync(session.Client, patient, "lab", "Complete blood count (CBC)", Cbc);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, warned.StatusCode);
        var html = await warned.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("data-order-duplicate", html);
        Assert.Contains("An active order for <strong>Complete blood count (CBC)</strong> was signed", html);
        Assert.Contains("Reason for duplicate order <span>(required)</span>", html);
        Assert.Contains("name=\"DuplicateReason\"", html);
        Assert.Contains("value=\"Synthetic fatigue\"", html);
        Assert.Equal(1, await OrdersAsync(apps, patient));

        using var confirmed = await SignAsync(session.Client, patient, "lab", "Complete blood count (CBC)", Cbc, reason: "Synthetic repeat: sample clotted");
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(2, await OrdersAsync(apps, patient, "SYN-LAB-001"));

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => SignAsync(session.Client, patient, "lab", "Lipid panel", Cbc)));
        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.UnprocessableEntity }));
            Assert.Equal(1, await OrdersAsync(apps, patient, "SYN-LAB-004"));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }
}
