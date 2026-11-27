using System.Net;
using Ehr.Data.Accounts;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RefillRequestTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC21;

// Test Case ID: TC-UC21-03
// Purpose / scenario: An answer that can't be given is refused with a sentence and nothing saved: a decline with no
//   message or one over 1,000 characters; an approval with another drug; an answer to a request already answered; an
//   approval once the medication was stopped, which the request's page no longer offers; a clinician without the
//   patient on their panel, sent to the chart; another patient's request, not found; the order form asked to approve
//   while changing an order.
// Type: Functional (invalid input)
// Requirements: proposed FR-58
// Use case: UC 21, Answer a refill request
// Preconditions: Dr. Test has the patient on their panel, signed in on the on-site app; a colleague doesn't. The patient
//   has asked to renew lisinopril and amlodipine. Another patient of Dr. Test's has a request too.
// Inputs and system conditions: POST /patients/{id}/refills/{request}?handler=Decline; GET and POST
//   /patients/{id}/orders/new?type=medication&refill={request}; GET /patients/{id}/refills/{request}; on the on-site app.
// Test data: Atorvastatin 20 mg tablet, another drug, posted though the form's item can't be changed; a message of 1,001
//   characters.
// Steps:
//   1. Decline lisinopril's request with no message, then with 1,001 characters.
//   2. Approve it with Atorvastatin 20 mg tablet.
//   3. Have amlodipine stopped; open its request and its order form, and post that form.
//   4. Decline lisinopril's request; then approve it and decline it again.
//   5. As the colleague, open the request; as Dr. Test, open the other patient's request under this patient.
//   6. Open the order form with both a refill and an order to replace.
// Expected result: Step 1: 422, "Message to the patient is required" and "Message to the patient must be 1,000 characters or
//   fewer.". Step 2: 422, "Item must be lisinopril 10 MG Oral Tablet for this refill."; no order saved. Step 3: the page says
//   "Can't be approved." and offers only Decline; the form goes back to the request; its post answers 409 and orders
//   nothing. Step 4: the decline is saved; the approval answers 409, "This refill request was already answered.", and
//   orders nothing; the second decline answers 409, "Not saved. Someone answered this request first.". Step 5: the
//   colleague lands on the chart's reason page, not the request; the other patient's request is not found (404).
//   Step 6: 400.
// Creator: Tahseen Arnan
public class TC_UC21_03_InvalidInput(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task An_answer_that_can_not_be_given_is_refused_and_nothing_is_saved()
    {
        var (_, session, patient) = await PortalPatientAsync(apps, "Unanswerable");
        await MedicationAsync(apps, patient.Patient);
        var amlodipine = await MedicationAsync(apps, patient.Patient, "amlodipine 5 MG Oral Tablet", "197361");
        using (var asked = await AskAsync(patient.Client, Lisinopril)) { Assert.Equal(PortalPath, PathOf(asked)); }
        var lisinoprilRequest = await RequestIdAsync(apps, patient.Patient);
        using (var asked = await AskAsync(patient.Client, "amlodipine 5 MG Oral Tablet")) { Assert.Equal(PortalPath, PathOf(asked)); }
        var amlodipineRequest = await RequestIdAsync(apps, patient.Patient);
        var (_, _, other) = await PortalPatientAsync(apps, "Unanswerableother");
        await MedicationAsync(apps, other.Patient);
        using (var asked = await AskAsync(other.Client, Lisinopril)) { Assert.Equal(PortalPath, PathOf(asked)); }
        var otherRequest = await RequestIdAsync(apps, other.Patient);

        foreach (var (message, error) in new[] { ("  ", "Message to the patient is required"), (new string('x', 1001), "Message to the patient must be 1,000 characters or fewer.") })
        {
            using var refused = await DeclineAsync(session.Client, patient.Patient, lisinoprilRequest, message);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Contains(error, Said(await refused.Content.ReadAsStringAsync(Cancel)));
        }

        using (var refused = await ApproveAsync(session.Client, patient.Patient, lisinoprilRequest, "Atorvastatin 20 mg tablet"))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Contains("Item must be lisinopril 10 MG Oral Tablet for this refill.", Said(await refused.Content.ReadAsStringAsync(Cancel)));
        }

        Assert.Equal(["0"], await ProxyTests.QueryAsync(apps, "SELECT count(*)::text FROM ehr.orders WHERE patient_id = @patient", patient.Patient));

        await ProxyTests.QueryAsync(apps,
            $"WITH stopped AS (UPDATE ehr.medication_statements SET status = 'stopped' WHERE id = '{amlodipine}' RETURNING id::text) SELECT id FROM stopped", patient.Patient);
        var page = await session.Client.GetStringAsync(ReviewPath(patient.Patient, amlodipineRequest), Cancel);
        Assert.Contains("Can't be approved. The medication is no longer active on the chart (Stopped).", Said(page));
        Assert.DoesNotContain("data-refill-approve", page);
        Assert.Contains("Decline request", page);
        using (var form = await session.Client.GetAsync(ApprovePath(patient.Patient, amlodipineRequest), Cancel))
        {
            Assert.Equal(ReviewPath(patient.Patient, amlodipineRequest), PathOf(form));
        }

        var token = Token(await session.Client.GetStringAsync(OrderTests.FormPath(patient.Patient, "medication"), Cancel));
        using (var refused = await session.Client.PostAsync(ApprovePath(patient.Patient, amlodipineRequest), Form(("__RequestVerificationToken", token),
            ("Item", "Amlodipine 5 mg tablet"), ("SafetySeen", "true"), ("Details[dose]", "5 mg"), ("Details[route]", "By mouth"), ("Details[frequency]", "Once a day")), Cancel))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("This refill request can't be approved.", Said(await refused.Content.ReadAsStringAsync(Cancel)));
        }

        var formBefore = await session.Client.GetStringAsync(ApprovePath(patient.Patient, lisinoprilRequest), Cancel);
        using (var declined = await DeclineAsync(session.Client, patient.Patient, lisinoprilRequest))
        {
            Assert.Contains("Declined.", Said(await ReadAsync(declined)));
        }

        using (var late = await session.Client.PostAsync(ApprovePath(patient.Patient, lisinoprilRequest), Form(("__RequestVerificationToken", Token(formBefore)),
            ("Item", "Lisinopril 10 mg tablet"), ("SafetySeen", "true"), ("Details[dose]", "10 mg"), ("Details[route]", "By mouth"), ("Details[frequency]", "Once a day")), Cancel))
        {
            Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
            Assert.Contains("This refill request was already answered.", Said(await late.Content.ReadAsStringAsync(Cancel)));
        }

        var reviewBefore = await session.Client.GetStringAsync(ReviewPath(patient.Patient, amlodipineRequest), Cancel);
        using (var first = await DeclineAsync(session.Client, patient.Patient, amlodipineRequest)) { Assert.Equal(HttpStatusCode.OK, first.StatusCode); }
        using (var again = await session.Client.PostAsync(ReviewPath(patient.Patient, amlodipineRequest) + "?handler=Decline",
            Form(("__RequestVerificationToken", Token(reviewBefore)), ("Message", "Synthetic: a second answer.")), Cancel))
        {
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Contains("Not saved. Someone answered this request first.", Said(await again.Content.ReadAsStringAsync(Cancel)));
        }

        Assert.Equal(["0"], await ProxyTests.QueryAsync(apps, "SELECT count(*)::text FROM ehr.orders WHERE patient_id = @patient", patient.Patient));
        Assert.All(await RequestsAsync(apps, patient.Patient), request => Assert.StartsWith("rejected|", request, StringComparison.Ordinal));

        var colleague = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Dr. Elsewhere", StaffRoles.Clinician));
        using (var off = await colleague.Client.GetAsync(ReviewPath(patient.Patient, lisinoprilRequest), Cancel))
        {
            Assert.StartsWith($"/patients/{patient.Patient}", PathOf(off), StringComparison.Ordinal);
            Assert.DoesNotContain("refills", PathOf(off), StringComparison.Ordinal);
        }

        using (var notFound = await session.Client.GetAsync(ReviewPath(patient.Patient, otherRequest), Cancel))
        {
            Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        }

        using (var both = await session.Client.GetAsync(ApprovePath(patient.Patient, lisinoprilRequest) + $"&replaces={Guid.NewGuid()}", Cancel))
        {
            Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        }
    }
}
