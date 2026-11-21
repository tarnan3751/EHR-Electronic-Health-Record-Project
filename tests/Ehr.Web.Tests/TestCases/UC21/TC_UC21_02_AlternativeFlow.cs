using System.Net;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RefillRequestTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC21;

// Test Case ID: TC-UC21-02
// Purpose / scenario: Approving goes through every check an order has, nothing bypassed: an active order for the same
//   item needs the duplicate's reason (FR-37) and an allergy the override's (FR-47), and both are kept on the order as
//   any order's are. A clinician may instead decline, with a message for the patient: the request's page then shows it,
//   under their name, and the item is done.
// Type: Functional (alternative flow)
// Requirements: FR-05, proposed FR-37, FR-47 and FR-58
// Use case: UC 21, Answer a refill request
// Preconditions: Dr. Test has the patient on their panel, signed in on the on-site app. The patient takes lisinopril
//   and amlodipine, and Dr. Test signed an order for Lisinopril 10 mg tablet earlier; the patient has since been
//   recorded as allergic to lisinopril. The patient has asked to renew both medications.
// Inputs and system conditions: POST /patients/{id}/orders/new?type=medication&refill={request} without reasons, then
//   with them; POST /patients/{id}/refills/{request}?handler=Decline; on the on-site app.
// Test data: the reasons "Synthetic: renewal of the current prescription." and "Synthetic: tolerated for years."; the
//   message "Synthetic: we stopped amlodipine at your last visit."
// Steps:
//   1. Approve the lisinopril request without reasons.
//   2. Approve it with both reasons.
//   3. Decline the amlodipine request with the message.
// Expected result: Step 1: 422, the form says the item is already ordered and that there are warnings to check, and
//   asks for both reasons; nothing is saved and the request still waits. Step 2: the request is approved; its order
//   keeps both reasons and the allergy warning. Step 3: the page says "Declined:" and shows the message under Dr. Test's
//   name; the request is declined with the message; every item for both requests is done.
// Creator: Tahseen Arnan
public class TC_UC21_02_AlternativeFlow(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

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
            Assert.Contains("Already ordered", words);
            Assert.Contains("Cancel, or give a reason and sign to order it again.", words);
            Assert.Contains("Give a reason to sign this order despite the warnings, or cancel.", words);
            Assert.Contains("Approves: the refill request for", words);
        }

        Assert.StartsWith("requested|", (await RequestsAsync(apps, patient.Patient))[0], StringComparison.Ordinal);

        using (var approved = await ApproveAsync(session.Client, patient.Patient, lisinopril,
            safetyReason: "Synthetic: tolerated for years.", duplicateReason: "Synthetic: renewal of the current prescription."))
        {
            Assert.Contains("Approved:", Said(await ReadAsync(approved)));
        }

        Assert.Equal(["Synthetic: renewal of the current prescription.|Synthetic: tolerated for years.|allergy"], await ProxyTests.QueryAsync(apps,
            $"""
            SELECT o.duplicate_reason || '|' || o.safety_override_reason || '|' || (SELECT string_agg(DISTINCT w->>'kind', ',') FROM jsonb_array_elements(o.safety_warnings) w WHERE w->>'kind' = 'allergy')
            FROM ehr.orders o JOIN ehr.refill_requests r ON r.order_id = o.id WHERE r.id = '{lisinopril}' AND o.patient_id = @patient
            """, patient.Patient));

        using (var declined = await DeclineAsync(session.Client, patient.Patient, amlodipine, "Synthetic: we stopped amlodipine at your last visit."))
        {
            var words = Said(await ReadAsync(declined));
            Assert.Contains("Declined: the patient reads your message in the portal.", words);
            Assert.Contains("Declined by Dr. Test", words);
            Assert.Contains("Synthetic: we stopped amlodipine at your last visit.", words);
            Assert.DoesNotContain("Decline request", words);
        }

        Assert.Equal($"rejected|amlodipine 5 MG Oral Tablet|-|{patient.Account}|-|Synthetic: we stopped amlodipine at your last visit.|{clinician.Id}",
            (await RequestsAsync(apps, patient.Patient))[1]);
        Assert.All([.. await ItemsAsync(apps, patient.Patient, lisinopril), .. await ItemsAsync(apps, patient.Patient, amlodipine)],
            item => Assert.EndsWith(" true", item));
    }
}
