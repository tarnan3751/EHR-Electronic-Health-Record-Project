using System.Net;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.DictationProposalTests;
using static Ehr.Web.Tests.RefillRequestTests;

namespace Ehr.Web.Tests.TestCases.UC1;

// Test Case ID: TC-UC1-23
// Purpose / scenario: The order form opened from an order a dictation proposed (step 15): it shows the clinician's
//   words, labelled as heard and marked in the sentence they said them in, as proposed from dictation, and fills in
//   nothing from them, with what was heard after them (step 15.1); the type, the item and the details are the clinician's, and every check of an order applies unchanged: an active order for the
//   same product under the clinic's other catalog code needs the duplicate's reason (FR-05), and an allergy needs the
//   override's (FR-47). Signing it signs an ordinary order and decides the proposal with it, and goes back to the review.
// Type: Functional (successful flow, and the checks of an order)
// Requirements: FR-05, FR-14 (reworded), proposed FR-47
// Use case: UC 1, Place an order
// Preconditions: Dr. Test has the patient on their panel, signed in on the on-site app, and dictated a note whose review
//   proposes "Go ahead and order a lipid panel"; later, after signing Lisinopril 10 mg tablet (the clinic's own item for
//   lisinopril 10 MG Oral Tablet) and the patient being recorded as allergic to lisinopril, a second such dictation.
// Inputs and system conditions: GET and POST /patients/{id}/orders/new?type={type}&proposal={proposal}, on the on-site
//   app.
// Test data: Lipid panel, reason for the test "Synthetic fatigue"; lisinopril 10 MG Oral Tablet, 50 mg by mouth once a
//   day; the reasons "Synthetic: a second prescription." and "Synthetic: tolerated for years."
// Steps:
//   1. Open the form from the first proposal; choose Lab.
//   2. Sign Lipid panel.
//   3. From the second proposal, sign lisinopril 10 MG Oral Tablet without reasons, then with both.
// Expected result: Step 1: "Proposed from dictation. Check every field. Nothing here was filled in from it." with the
//   words after "As heard:", marked after "Chest is clear.", and the allergy's sentence after "Heard after it:"; each type's link keeps the proposal; the item and the details are empty. Step 2:
//   back on the review; one active lab order for Lipid panel, signed by Dr. Test, decides the proposal. Step 3: first 422, "Already ordered" and the safety reason asked for, nothing saved and the
//   proposal waiting; then the order is signed, keeping both reasons, and decides the second proposal.
// Creator: Tahseen Arnan
public class TC_UC1_23_OrderFromDictation(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_order_form_opened_from_a_proposal_fills_in_nothing_and_signs_through_every_check()
    {
        var dictated = await DictateAsync(apps);
        var html = await dictated.Session.Client.GetStringAsync(OrderFormPath(dictated.Patient, dictated.Order), Cancel);
        var said = Said(html);
        Assert.Contains("Proposed from dictation. Check every field. Nothing here was filled in from it.", said);
        Assert.Contains($"As heard: Chest is clear. {DictationSidecarStub.OrderSaid}. Heard after it: {DictationSidecarStub.AllergySaid}.", said);
        Assert.Contains($"<mark class=\"sc-transcript__source\">{DictationSidecarStub.OrderSaid}</mark>", html);
        Assert.Contains("Choose the type of order.", said);
        Assert.Contains($"type=lab&amp;proposal={dictated.Order}", html);

        var lab = await dictated.Session.Client.GetStringAsync(OrderFormPath(dictated.Patient, dictated.Order, "lab"), Cancel);
        Assert.DoesNotMatch("name=\"Item\" value=\"[^\"]", lab);
        Assert.DoesNotMatch("name=\"Details\\[indication\\]\" value=\"[^\"]", lab);
        Assert.Contains("name=\"Item\"", lab);
        Assert.Contains($"<a href=\"{dictated.ReviewPath}\">Cancel</a>", lab);

        using (var signed = await UC1OrderAsync(dictated, "Lipid panel"))
        {
            Assert.Equal(dictated.ReviewPath, PathOf(signed));
        }

        var order = Guid.Parse((await QueryAsync(apps, "SELECT id FROM ehr.orders WHERE patient_id = @id", dictated.Patient)).Single());
        Assert.Equal($"ordered|{order}|-", await StateAsync(apps, dictated.Order));
        Assert.Equal(["lab SYN-LAB-004 active true"], await QueryAsync(apps,
            "SELECT type || ' ' || catalog_code || ' ' || status || ' ' || (signer_id IS NOT NULL) FROM ehr.orders WHERE patient_id = @id", dictated.Patient));

        // The same product under the clinic's other code, and an allergy: the order form's checks, unchanged.
        using (var earlier = await OrderTests.SignAsync(dictated.Session.Client, dictated.Patient, "medication", "Lisinopril 10 mg tablet", SafetyTests.Details, fetch: false))
        {
            Assert.Equal(HttpStatusCode.OK, earlier.StatusCode);
        }

        await SafetyTests.AllergyAsync(apps, dictated.Patient, "lisinopril");
        var second = await DictateAsync(apps, dictated.Session, dictated.Patient);
        using (var refused = await UC1OrderAsync(second, "lisinopril 10 MG Oral Tablet", "medication", SafetyTests.Details))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            var words = Said(await refused.Content.ReadAsStringAsync(Cancel));
            Assert.Contains("Already ordered An active order for Lisinopril 10 mg tablet was signed", words);
            Assert.Contains("Give a reason to sign this order despite the warnings, or cancel.", words);
            Assert.Contains("Proposed from dictation.", words);
        }

        Assert.Equal("pending|-|-", await StateAsync(apps, second.Order));
        using (var signed = await UC1OrderAsync(second, "lisinopril 10 MG Oral Tablet", "medication", SafetyTests.Details,
            "Synthetic: a second prescription.", "Synthetic: tolerated for years."))
        {
            Assert.Equal(second.ReviewPath, PathOf(signed));
        }

        Assert.Equal(["Synthetic: a second prescription.|Synthetic: tolerated for years."], await QueryAsync(apps,
            """
            SELECT o.duplicate_reason || '|' || o.safety_override_reason FROM ehr.orders o JOIN ehr.ai_chart_proposals p ON p.order_id = o.id
            WHERE p.id = @id AND o.catalog_code = '314076'
            """, second.Order));
    }
}
