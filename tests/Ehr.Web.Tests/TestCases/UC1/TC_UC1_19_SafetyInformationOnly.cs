using System.Net;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.SafetyTests;

namespace Ehr.Web.Tests.TestCases.UC1;

// Test Case ID: TC-UC1-19
// Purpose / scenario: What doesn't stop an order. A moderate interaction is shown and kept on the order without asking
//   for a reason; an allergy written without a code, and a drug of another class, raise nothing; a lab order isn't
//   checked. Off-site the same warning is shown from the cloud copy and the signing is forwarded.
// Type: Functional (alternative flow and exceptional condition)
// Requirements: FR-05, FR-33, NFR-08, proposed FR-47
// Use case: UC 1, Place an order
// Preconditions: The clinician is signed in with the patient on their panel. The patient has an allergy to
//   "Penicillin" typed without a code, a coded allergy to ibuprofen, and an active order for metformin.
// Inputs and system conditions: GET and POST /patients/{id}/orders/new?type=medication and ?type=lab, on the on-site
//   app and on the cloud app.
// Test data: Orders for "Amoxicillin 500 mg capsule", "Lisinopril 10 mg tablet" and "Amlodipine 5 mg tablet".
// Steps:
//   1. Sign amoxicillin.
//   2. Open the lab order form.
//   3. Choose "Sign order" for lisinopril; choose it again. Then ask for the same order again off-site.
// Expected result: Step 1: signed with nothing found (the uncoded allergy can't be checked, and the form says
//   allergies and medications without a code aren't). Step 2: no line about safety checks. Step 3: the first press signs nothing and
//   brings the form back with "Be aware" and "Sign again to go ahead, or cancel.", the cursor on "Sign order" and no
//   reason field; the second signs; the order holds the moderate interaction with metformin from DDInter and no reason. Off-site, the form for the same
//   drug shows "Be aware" with "Interaction: lisinopril with metformin", "Moderate", "Source: DDInter 2.0" with a link
//   to DDInter's interaction checker, "These don't stop the order.", and no reason field.
// Creator: Tahseen Arnan
public class TC_UC1_19_SafetyInformationOnly(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_moderate_interaction_is_shown_and_kept_without_stopping_the_order()
    {
        // OnPanelAsync gives the patient an allergy to "Penicillin" with no code.
        var (_, patient, session) = await OnPanelAsync(apps);
        await AllergyAsync(apps, patient, "ibuprofen");

        using var uncoded = await SignAsync(session.Client, patient, "Amoxicillin 500 mg capsule");
        Assert.Equal($"/patients/{patient}", PathOf(uncoded));
        Assert.Equal(["-|-"], await OrdersAsync(apps, patient));
        var medicationForm = Words(await session.Client.GetStringAsync(FormPath(patient, "medication"), Cancel));
        Assert.Contains("Only items with a drug code are checked.", medicationForm);
        Assert.Contains("Allergies and medications without a code aren't checked.", medicationForm);
        Assert.Contains("No warning doesn't mean a combination is safe.", medicationForm);
        Assert.DoesNotContain("data-safety-disclaimer", await session.Client.GetStringAsync(FormPath(patient, "lab"), Cancel));

        await SignedAsync(session.Client, patient, "medication", "Metformin 500 mg tablet", Details);

        // The first "Sign order" brings the note up and signs nothing; the second signs, with no reason asked.
        using var first = await SignAsync(session.Client, patient, "Lisinopril 10 mg tablet");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, first.StatusCode);
        var notes = await first.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("data-order-safety=\"information\"", notes);
        Assert.Contains("These don't stop the order. They are kept on it. Sign again to go ahead, or cancel.", Words(notes));
        Assert.Contains("name=\"SafetySeen\" value=\"true\"", notes);
        Assert.DoesNotContain("name=\"SafetyReason\"", notes);
        Assert.DoesNotContain("aria-invalid", notes);
        Assert.Matches("<button [^>]*data-order-sign[^>]*autofocus", notes);
        Assert.Equal(2, (await OrdersAsync(apps, patient)).Count);
        using var second = await SignAsync(session.Client, patient, "Lisinopril 10 mg tablet", seen: true);
        Assert.Equal($"/patients/{patient}", PathOf(second));
        Assert.Equal(["-|-", "-|-", "-|interaction:moderate:ddinter"], await OrdersAsync(apps, patient));

        // Off-site, asking for lisinopril again: a duplicate now, which needs its own reason, and the interaction beside it.
        var cloud = Client(apps.Cloud, session.Jar);
        using var again = await SignAsync(cloud, patient, "Lisinopril 10 mg tablet");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        var page = await again.Content.ReadAsStringAsync(Cancel);
        var text = Words(page);
        Assert.Contains("data-order-safety=\"information\"", page);
        Assert.Contains("Be aware", text);
        Assert.Contains("Interaction: lisinopril with metformin", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<span class=\"sc-tag sc-tag--warning\">Moderate</span>", page);
        Assert.Contains("Use together with caution: monitor the patient, and consider adjusting a dose.", text);
        Assert.Contains("Source: DDInter 2.0.", text);
        Assert.Contains("href=\"https://ddinter2.scbdd.com/inter-checker/\"", page);
        Assert.Contains("These don't stop the order. They are kept on it.", text);
        Assert.DoesNotContain("name=\"SafetyReason\"", page);
        Assert.Contains("Already ordered", page);
    }
}
