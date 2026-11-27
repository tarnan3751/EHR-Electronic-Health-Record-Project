using System.Net;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.DictationProposalTests;
using static Ehr.Web.Tests.RefillRequestTests;

namespace Ehr.Web.Tests.TestCases.UC1;

// Test Case ID: TC-UC1-24
// Purpose / scenario: An order a dictation proposed (step 15) is decided once. Opened in two tabs, the second "Sign
//   order" saves nothing, the order included, and says so.
//   A proposal decided already sends the form back to the review; one that is an allergy's, another clinician's, or
//   combined with a refill or a change doesn't open the form; nor one named on the form of another patient on the same
//   clinician's panel. (One dismissed on the review while its form was open is TC-UC13-11's.)
// Type: Functional (exceptional condition)
// Requirements: FR-14 (reworded), FR-05
// Use case: UC 1, Place an order
// Preconditions: Dr. Test has the patient on their panel, signed in on the on-site app, and dictated a note whose review
//   proposes an order and an allergy to add.
// Inputs and system conditions: GET and POST /patients/{id}/orders/new?type=lab&proposal={proposal}, with refill= or
//   replaces= too; on the on-site app, as Dr. Test and as a colleague with the patient on their panel.
// Test data: Lipid panel and Hemoglobin A1c, reason for the test "Synthetic fatigue".
// Steps:
//   1. Open the lab form from the order's proposal in two tabs; sign Lipid panel in the first, Hemoglobin A1c in the second.
//   2. Open the form from the order's proposal again; from the allergy's, and sign from it; with a refill; with a change.
//   3. As the colleague, open the form from a second dictation's order proposal.
//   4. As Dr. Test, open another patient's order form, of their panel too, with the second dictation's order proposal;
//      sign from it, the proposal in the address and then in the form.
// Expected result: Step 1: the first signs and goes back to the review; the second is 409, "Not saved. This proposal was
//   already decided.", and the patient has the first order alone. Step 2: back
//   to the review; to the chart, and 404 with nothing signed; 400; 400. Step 3: the colleague is sent to the chart,
//   shown nothing of it, and the proposal still waits. Step 4: to that patient's chart, then 404 twice; nothing is
//   ordered for either patient, the proposal still waits, and no read of it or its review is recorded against the other
//   patient.
// Creator: Tahseen Arnan
public class TC_UC1_24_OrderFromDictationRefused(WebApps apps) : IClassFixture<WebApps>
{
    // "Sign order" for a lab test on a form already open in a tab, with its own token.
    static Task<HttpResponseMessage> SignOnAsync(Dictated dictated, string path, string token, string item) =>
        dictated.Session.Client.PostAsync(path, Form([("__RequestVerificationToken", token), ("Item", item), ("SafetySeen", "true"),
            .. OrderTests.Cbc.Select(d => ($"Details[{d.Item1}]", (object)d.Item2))]), Cancel);

    [Fact]
    public async Task A_proposed_order_is_signed_once_and_a_second_tab_saves_nothing()
    {
        var dictated = await DictateAsync(apps);
        var path = OrderFormPath(dictated.Patient, dictated.Order, "lab");
        var first = Token(await dictated.Session.Client.GetStringAsync(path, Cancel));
        var second = Token(await dictated.Session.Client.GetStringAsync(path, Cancel));
        using (var signed = await SignOnAsync(dictated, path, first, "Lipid panel"))
        {
            Assert.Equal(dictated.ReviewPath, PathOf(signed));
        }

        using (var refused = await SignOnAsync(dictated, path, second, "Hemoglobin A1c"))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("Not saved. This proposal was already decided.", Said(await refused.Content.ReadAsStringAsync(Cancel)));
        }

        Assert.Equal(["SYN-LAB-004"], await QueryAsync(apps, "SELECT catalog_code FROM ehr.orders WHERE patient_id = @id", dictated.Patient));
        Assert.StartsWith("ordered|", await StateAsync(apps, dictated.Order), StringComparison.Ordinal);

        // Decided already: the form goes back to the review. An allergy's proposal isn't the order form's.
        using (var decided = await dictated.Session.Client.GetAsync(path, Cancel))
        {
            Assert.Equal(dictated.ReviewPath, PathOf(decided));
        }

        using (var allergy = await dictated.Session.Client.GetAsync(OrderFormPath(dictated.Patient, dictated.Allergy, "lab"), Cancel))
        {
            Assert.Equal($"/patients/{dictated.Patient}", PathOf(allergy));
        }

        using (var allergy = await SignOnAsync(dictated, OrderFormPath(dictated.Patient, dictated.Allergy, "lab"), first, "Hemoglobin A1c"))
        {
            Assert.Equal(HttpStatusCode.NotFound, allergy.StatusCode);
        }

        // Never a refill or a change as well.
        var order = (await QueryAsync(apps, "SELECT id FROM ehr.orders WHERE patient_id = @id", dictated.Patient)).Single();
        foreach (var with in new[] { $"&refill={Guid.NewGuid()}", $"&replaces={order}" })
        {
            using var refused = await dictated.Session.Client.GetAsync(path + with, Cancel);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        // Another clinician's proposal, though the patient is on their panel too: an ordinary form, nothing of the proposal.
        var again = await DictateAsync(apps, dictated.Session, dictated.Patient);
        var colleague = await apps.CreateStaffAsync("Dr. Colleague", Ehr.Data.Accounts.StaffRoles.Clinician);
        await apps.AssignAsync(colleague, dictated.Patient);
        var theirs = await SignInAsync(apps.OnPrem, colleague);
        using (var form = await theirs.Client.GetAsync(OrderFormPath(again.Patient, again.Order), Cancel))
        {
            Assert.Equal($"/patients/{again.Patient}", PathOf(form));
            var html = await ReadAsync(form);
            Assert.DoesNotContain(DictationSidecarStub.OrderSaid, html);
            Assert.DoesNotContain("Proposed from dictation", html);
        }

        Assert.Equal(["SYN-LAB-004"], await QueryAsync(apps, "SELECT catalog_code FROM ehr.orders WHERE patient_id = @id", dictated.Patient));
        Assert.Equal("pending|-|-", await StateAsync(apps, again.Order));

        // Named on the form of another patient on Dr. Test's panel: not that patient's proposal.
        var other = await apps.InsertPatientAsync("Synthetic", "Elsewhere");
        await apps.AssignAsync(await apps.ClinicianAsync(), other);
        using (var form = await dictated.Session.Client.GetAsync(OrderFormPath(other, again.Order, "lab"), Cancel))
        {
            Assert.Equal($"/patients/{other}", PathOf(form));
        }

        using (var inAddress = await SignOnAsync(dictated, OrderFormPath(other, again.Order, "lab"), first, "Hemoglobin A1c"))
        {
            Assert.Equal(HttpStatusCode.NotFound, inAddress.StatusCode);
        }

        using (var inForm = await dictated.Session.Client.PostAsync($"/patients/{other}/orders/new?type=lab", Form([("__RequestVerificationToken", first),
            ("Proposal", again.Order), ("Item", "Hemoglobin A1c"), ("SafetySeen", "true"), .. OrderTests.Cbc.Select(d => ($"Details[{d.Item1}]", (object)d.Item2))]), Cancel))
        {
            Assert.Equal(HttpStatusCode.NotFound, inForm.StatusCode);
        }

        Assert.Equal("0 orders, 0 allergies", await ChartAsync(apps, other));
        Assert.Equal(["SYN-LAB-004"], await QueryAsync(apps, "SELECT catalog_code FROM ehr.orders WHERE patient_id = @id", dictated.Patient));
        Assert.Equal("pending|-|-", await StateAsync(apps, again.Order));
        Assert.Equal(["0"], await QueryAsync(apps,
            "SELECT count(*) FROM ehr.audit_events WHERE patient_id = @id AND table_name IN ('ai_chart_proposals', 'ai_note_reviews')", other));
    }
}
