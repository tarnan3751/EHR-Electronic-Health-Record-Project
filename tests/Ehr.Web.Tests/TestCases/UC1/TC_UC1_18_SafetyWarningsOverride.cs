using System.Net;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.SafetyTests;

namespace Ehr.Web.Tests.TestCases.UC1;

// Test Case ID: TC-UC1-18
// Purpose / scenario: A medication order is checked against the patient's coded allergies and what they take. An
//   allergy and a serious interaction each stop the order until the clinician gives a reason; each warning names the
//   problem, its severity, one line of advice and its source; the reason is kept on the order with what was found.
// Type: Functional (alternative flow: the order has safety warnings)
// Requirements: FR-05, NFR-08, proposed FR-47
// Use case: UC 1, Place an order
// Preconditions: The clinician is signed in on the on-site app with two patients on their panel. The first has a coded
//   allergy to amoxicillin. The second takes linezolid and phenelzine, coded on the chart.
// Inputs and system conditions: GET and POST /patients/{id}/orders/new?type=medication, as plain form posts.
// Test data: Orders for "Amoxicillin 500 mg capsule" and "Sertraline 50 mg tablet". Reasons "Synthetic: allergy ruled
//   out by testing" and "Synthetic: linezolid stops today".
// Steps:
//   1. For the first patient, sign amoxicillin with no reason.
//   2. Sign it again with a reason.
//   3. For the second patient, sign sertraline with no reason; then with a reason.
// Expected result: Step 1: answered 422; "Check before signing" lists "Allergy: Amoxicillin", tagged "Major", with
//   advice and "Source: The patient's chart", and a required field "Reason for ordering despite the warnings", marked
//   with "Give a reason to sign this order despite the warnings, or cancel."; nothing is signed. Step 2: signed; the
//   order holds the reason and the allergy found. Step 3: 422, listing the interaction with phenelzine first ("High
//   priority", "Source: ONC high-priority list, by way of Wikidata") and then with linezolid ("Major", "Source: FDA
//   label", what the sertraline label says, and the label's DailyMed address); then signed, holding the reason and
//   both findings. The form always says the checks are decision support for this demonstration system, not a
//   complete clinical reference.
// Creator: Tahseen Arnan
public class TC_UC1_18_SafetyWarningsOverride(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_allergy_or_a_serious_interaction_stops_the_order_until_a_reason_is_given()
    {
        var (clinician, allergic, session) = await OnPanelAsync(apps);
        await AllergyAsync(apps, allergic, "amoxicillin");
        var form = await session.Client.GetStringAsync(FormPath(allergic, "medication"), Cancel);
        Assert.Contains("Decision support for this demonstration system, not a complete clinical reference.", form);
        Assert.DoesNotContain("data-order-safety", form);

        using var stopped = await SignAsync(session.Client, allergic, "Amoxicillin 500 mg capsule");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stopped.StatusCode);
        var warned = await stopped.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("data-order-safety=\"reason\"", warned);
        Assert.Contains("<h3>Check before signing</h3>", warned);
        Assert.Contains("<strong>Allergy: Amoxicillin</strong>", warned);
        Assert.Contains("<span class=\"sc-tag sc-tag--warning\">Major</span>", warned);
        Assert.Contains("The chart records an allergy to Amoxicillin.", Words(warned));
        Assert.Contains("Source: The patient's chart.", Words(warned));
        Assert.Matches("<textarea id=\"order-safety-reason\"[^>]*name=\"SafetyReason\"[^>]*required[^>]*aria-invalid=\"true\"", warned);
        Assert.Contains("Give a reason to sign this order despite the warnings, or cancel.", warned);
        // What was typed is still there.
        Assert.Contains("value=\"Amoxicillin 500 mg capsule\"", warned);
        Assert.Empty(await OrdersAsync(apps, allergic));

        using var signed = await SignAsync(session.Client, allergic, "Amoxicillin 500 mg capsule", "Synthetic: allergy ruled out by testing");
        Assert.Equal($"/patients/{allergic}", PathOf(signed));
        Assert.Equal(["Synthetic: allergy ruled out by testing|allergy:major:chart"], await OrdersAsync(apps, allergic));

        var treated = await apps.InsertPatientAsync("Synthetic", "Interacting");
        await apps.AssignAsync(clinician, treated);
        await TakingAsync(apps, treated, "linezolid");
        await TakingAsync(apps, treated, "phenelzine");
        using var interacting = await SignAsync(session.Client, treated, "Sertraline 50 mg tablet");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, interacting.StatusCode);
        var page = await interacting.Content.ReadAsStringAsync(Cancel);
        var text = Words(page);
        var highPriority = text.IndexOf("Interaction: sertraline with phenelzine", StringComparison.OrdinalIgnoreCase);
        var major = text.IndexOf("Interaction: sertraline with linezolid", StringComparison.OrdinalIgnoreCase);
        Assert.True(highPriority > 0 && major > highPriority, "The high-priority interaction is listed before the major one.");
        Assert.Contains("High priority", text);
        Assert.Contains("On the list of interactions an electronic record should always stop for. Avoid the combination.", text);
        Assert.Contains("Source: ONC high-priority list, by way of Wikidata.", text);
        Assert.Contains("Avoid the combination unless the benefit clearly outweighs the risk.", text);
        Assert.Contains("The sertraline label contraindicates sertraline in patients taking a monoamine oxidase inhibitor, and names linezolid as one", text);
        Assert.Contains("Source: FDA label. FDA label: SERTRALINE HYDROCHLORIDE", text);
        Assert.Contains("https://dailymed.nlm.nih.gov/dailymed/drugInfo.cfm?setid=", text);
        Assert.Empty(await OrdersAsync(apps, treated));

        using var overridden = await SignAsync(session.Client, treated, "Sertraline 50 mg tablet", "Synthetic: linezolid stops today");
        Assert.Equal($"/patients/{treated}", PathOf(overridden));
        var kept = Assert.Single(await OrdersAsync(apps, treated));
        Assert.StartsWith("Synthetic: linezolid stops today|", kept);
        Assert.Contains("interaction:high-priority:onc", kept);
        Assert.Contains("interaction:major:fda-label", kept);
    }
}
