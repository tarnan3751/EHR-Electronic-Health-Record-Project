using System.Net;
using Ehr.Data.Accounts;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RefillRequestTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC21;

// Test Case ID: TC-UC21-04
// Purpose / scenario: Off-site, the request is read from the cloud copy and a decline is forwarded to the clinic's
//   server. While that can't be reached, the request stays readable, its answers are replaced by a sentence, and a
//   decline sent from a page opened before gets the "Not saved" page with the message to copy. Two clinicians answering
//   at once give one answer: a colleague declines while the order form is open, and signing it then orders nothing.
// Type: Functional (exceptional condition)
// Requirements: NFR-08, proposed FR-58
// Use case: UC 21, Answer a refill request (with UC 2, Read records during an on-site outage)
// Preconditions: Dr. Test and a colleague have the patient on their panels, signed in. The patient has asked to renew
//   lisinopril and amlodipine.
// Inputs and system conditions: GET and POST /patients/{id}/refills/{request} on the cloud app, then on the cloud app
//   cut off from the clinic's server; GET and POST /patients/{id}/orders/new?type=medication&refill={request} on the
//   on-site app.
// Test data: the messages "Synthetic: declined off-site." and "Synthetic <message> during the outage.".
// Steps:
//   1. On the cloud app, decline the amlodipine request.
//   2. Open the lisinopril request on the clinic's server; cut off, send a decline; cut off, open the request.
//   3. As Dr. Test, open the order form for the lisinopril request; as the colleague, decline it; as Dr. Test, sign.
// Expected result: Step 1: the decline is forwarded and saved. Step 2: 503 and the "Not saved" page with the message;
//   cut off, the page shows the request and "Answering a refill request needs the clinic's server.", with no answer
//   to give. Step 3: 409, "This refill request was already answered."; the request is declined by
//   the colleague, and the patient has no order.
// Creator: Tahseen Arnan
public class TC_UC21_04_ExceptionalCondition(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Off_site_an_answer_is_forwarded_during_an_outage_none_is_offered_and_one_answer_stands()
    {
        var (_, session, patient) = await PortalPatientAsync(apps, "Outage");
        var colleagueAccount = await apps.CreateStaffAsync("Dr. Colleague", StaffRoles.Clinician);
        await apps.AssignAsync(colleagueAccount, patient.Patient);
        var colleague = await SignInAsync(apps.OnPrem, colleagueAccount);
        await MedicationAsync(apps, patient.Patient);
        await MedicationAsync(apps, patient.Patient, "amlodipine 5 MG Oral Tablet", "197361");
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

        using (var declined = await DeclineAsync(Client(apps.Cloud, session.Jar), patient.Patient, amlodipine, "Synthetic: declined off-site."))
        {
            Assert.Equal(HttpStatusCode.OK, declined.StatusCode);
        }

        Assert.Contains($"POST {ReviewPath(patient.Patient, amlodipine)}?handler=Decline", apps.Forwarded);
        Assert.Contains("|Synthetic: declined off-site.|", (await RequestsAsync(apps, patient.Patient))[1]);

        var token = Token(await session.Client.GetStringAsync(ReviewPath(patient.Patient, lisinopril), Cancel));
        var cutOff = Client(apps.CloudCutOff, session.Jar);
        using (var request = new HttpRequestMessage(HttpMethod.Post, ReviewPath(patient.Patient, lisinopril) + "?handler=Decline")
        {
            Content = Form(("Message", "Synthetic <message> during the outage."), ("__RequestVerificationToken", token)),
        })
        {
            request.Headers.Add("Sec-Fetch-Site", "same-origin");
            using var refused = await cutOff.SendAsync(request, Cancel);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            var notSaved = await refused.Content.ReadAsStringAsync(Cancel);
            Assert.Contains("Not saved.</span> The clinic's server can't be reached.", notSaved);
            Assert.Contains("Synthetic &lt;message&gt; during the outage.", notSaved);
        }

        var readOnly = await cutOff.GetStringAsync(ReviewPath(patient.Patient, lisinopril), Cancel);
        Assert.Contains("Answering a refill request needs the clinic's server.", Said(readOnly));
        Assert.Contains(Lisinopril, Said(readOnly));
        Assert.DoesNotContain("data-refill-approve", readOnly);
        Assert.DoesNotContain("data-refill-decline", readOnly);

        var form = await session.Client.GetStringAsync(ApprovePath(patient.Patient, lisinopril), Cancel);
        using (var declined = await DeclineAsync(colleague.Client, patient.Patient, lisinopril, "Synthetic: answered by a colleague."))
        {
            Assert.Equal(HttpStatusCode.OK, declined.StatusCode);
        }

        using (var late = await session.Client.PostAsync(ApprovePath(patient.Patient, lisinopril), Form(("__RequestVerificationToken", Token(form)),
            ("Item", "Lisinopril 10 mg tablet"), ("SafetySeen", "true"), ("Details[dose]", "10 mg"), ("Details[route]", "By mouth"), ("Details[frequency]", "Once a day")), Cancel))
        {
            Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
            Assert.Contains("This refill request was already answered.", Said(await late.Content.ReadAsStringAsync(Cancel)));
        }

        Assert.Equal($"rejected|{Lisinopril}|-|{patient.Account}|-|Synthetic: answered by a colleague.|{colleagueAccount.Id}", (await RequestsAsync(apps, patient.Patient))[0]);
        Assert.Equal(["0"], await ProxyTests.QueryAsync(apps, "SELECT count(*)::text FROM ehr.orders WHERE patient_id = @patient", patient.Patient));
    }
}
