using System.Net;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RefillRequestTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC21;

// Test Case ID: TC-UC21-02
// Purpose / scenario: Approving goes through every check an order has, nothing bypassed: an active order for the same
//   product needs the duplicate's reason (FR-05), even under the clinic's own item for it rather than RxNorm's, which
//   the approval orders, and an allergy needs the override's (FR-47); both are kept on the order as any order's are. A
//   renewal at another strength is not an approval: the form refuses it, and the clinician declines the request with a
//   message for the patient, which the request's page then shows under their name, and writes the new order from the
//   chart in the usual way.
// Type: Functional (alternative flow)
// Requirements: FR-05, proposed FR-47 and FR-58
// Use case: UC 21, Answer a refill request
// Preconditions: Dr. Test has the patient on their panel, signed in on the on-site app. The patient takes lisinopril
//   and amlodipine, and Dr. Test signed an order for Lisinopril 10 mg tablet earlier, the clinic's own item for
//   lisinopril 10 MG Oral Tablet; the patient has since been recorded as allergic to lisinopril. The patient has asked to renew both medications.
// Inputs and system conditions: POST /patients/{id}/orders/new?type=medication&refill={request} without reasons, then
//   with them, and with another strength; POST /patients/{id}/refills/{request}?handler=Decline; POST
//   /patients/{id}/orders/new?type=medication; on the on-site app.
// Test data: the reasons "Synthetic: renewal of the current prescription." and "Synthetic: tolerated for years."; the
//   message "Synthetic: we are raising your amlodipine to 10 mg; the new prescription is ordered."
// Steps:
//   1. Approve the lisinopril request without reasons.
//   2. Approve it with both reasons.
//   3. Approve the amlodipine request with amlodipine 10 MG Oral Tablet; decline it with the message; order amlodipine
//      10 MG Oral Tablet from the chart's order form.
// Expected result: Step 1: 422, the form says Lisinopril 10 mg tablet is already ordered and that there are warnings
//   to check, and asks for both reasons; nothing is saved and the request still waits. Step 2: the request is approved; its order
//   keeps both reasons and the allergy warning. Step 3: the approval is refused (422, "This order renews the same medication,
//   amlodipine 5 MG Oral Tablet.") with nothing saved; the page then says "Declined." and shows the message under Dr.
//   Test's name; the request is declined with the message; the new order is an ordinary one, answering no request; every
//   item for both requests is done.
// Creator: Tahseen Arnan
public class TC_UC21_02_AlternativeFlow(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Approving_keeps_every_check_of_an_order_and_declining_sends_a_message()
    {
        var (clinician, session, patient) = await PortalPatientAsync(apps, "Checked");
        await MedicationAsync(apps, patient.Patient);
        await MedicationAsync(apps, patient.Patient, "amlodipine 5 MG Oral Tablet", "197361");
        using (var earlier = await OrderTests.SignAsync(session.Client, patient.Patient, "medication", "Lisinopril 10 mg tablet", SafetyTests.Details, fetch: false))
        {
            Assert.Equal(HttpStatusCode.OK, earlier.StatusCode);
        }

        await SafetyTests.AllergyAsync(apps, patient.Patient, "lisinopril");
        using (var asked = await AskAsync(patient.Client, Lisinopril))
        {
            Assert.Equal(PortalPath, PathOf(asked));
        }

        var lisinopril = await RequestIdAsync(apps, patient.Patient);
        using (var asked = await AskAsync(patient.Client, "amlodipine 5 MG Oral Tablet"))
        {
            Assert.Equal(PortalPath, PathOf(asked));
        }

        var amlodipine = await RequestIdAsync(apps, patient.Patient);

        using (var refused = await ApproveAsync(session.Client, patient.Patient, lisinopril))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            var words = Said(await refused.Content.ReadAsStringAsync(Cancel));
            Assert.Contains("Already ordered An active order for Lisinopril 10 mg tablet was signed", words);
            Assert.Contains("Cancel, or give a reason and sign to order it again.", words);
            Assert.Contains("Give a reason to sign this order despite the warnings, or cancel.", words);
            Assert.Contains("Refill request. Signing approves the request for", words);
        }

        Assert.StartsWith("requested|", (await RequestsAsync(apps, patient.Patient))[0], StringComparison.Ordinal);

        using (var approved = await ApproveAsync(session.Client, patient.Patient, lisinopril,
            safetyReason: "Synthetic: tolerated for years.", duplicateReason: "Synthetic: renewal of the current prescription."))
        {
            Assert.Contains("Approved.", Said(await ReadAsync(approved)));
        }

        Assert.Equal(["Synthetic: renewal of the current prescription.|Synthetic: tolerated for years.|allergy"], await ProxyTests.QueryAsync(apps,
            $"""
            SELECT o.duplicate_reason || '|' || o.safety_override_reason || '|' || (SELECT string_agg(DISTINCT w->>'kind', ',') FROM jsonb_array_elements(o.safety_warnings) w WHERE w->>'kind' = 'allergy')
            FROM ehr.orders o JOIN ehr.refill_requests r ON r.order_id = o.id WHERE r.id = '{lisinopril}' AND o.patient_id = @patient
            """, patient.Patient));

        const string Raised = "Synthetic: we are raising your amlodipine to 10 mg; the new prescription is ordered.";
        var ordersBefore = await ProxyTests.QueryAsync(apps, "SELECT count(*)::text FROM ehr.orders WHERE patient_id = @patient", patient.Patient);
        using (var stronger = await ApproveAsync(session.Client, patient.Patient, amlodipine, "amlodipine 10 MG Oral Tablet"))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, stronger.StatusCode);
            Assert.Contains("This order renews the same medication, amlodipine 5 MG Oral Tablet.", Said(await stronger.Content.ReadAsStringAsync(Cancel)));
        }

        Assert.Equal(ordersBefore, await ProxyTests.QueryAsync(apps, "SELECT count(*)::text FROM ehr.orders WHERE patient_id = @patient", patient.Patient));

        using (var declined = await DeclineAsync(session.Client, patient.Patient, amlodipine, Raised))
        {
            var words = Said(await ReadAsync(declined));
            Assert.Contains("Declined.", words);
            Assert.Contains("Declined by Dr. Test", words);
            Assert.Contains(Raised, words);
            Assert.DoesNotContain("Decline request", words);
        }

        using (var ordered = await OrderTests.SignAsync(session.Client, patient.Patient, "medication", "amlodipine 10 MG Oral Tablet", SafetyTests.Details, fetch: false))
        {
            Assert.Equal($"/patients/{patient.Patient}", PathOf(ordered));
        }

        Assert.Equal($"rejected|amlodipine 5 MG Oral Tablet|-|{patient.Account}|-|{Raised}|{clinician.Id}", (await RequestsAsync(apps, patient.Patient))[1]);
        Assert.Equal(["0"], await ProxyTests.QueryAsync(apps,
            "SELECT count(*)::text FROM ehr.orders o WHERE o.patient_id = @patient AND o.catalog_code = '308135' AND EXISTS (SELECT 1 FROM ehr.refill_requests r WHERE r.order_id = o.id)",
            patient.Patient));
        Assert.All([.. await ItemsAsync(apps, patient.Patient, lisinopril), .. await ItemsAsync(apps, patient.Patient, amlodipine)],
            item => Assert.EndsWith(" true", item));
    }
}
