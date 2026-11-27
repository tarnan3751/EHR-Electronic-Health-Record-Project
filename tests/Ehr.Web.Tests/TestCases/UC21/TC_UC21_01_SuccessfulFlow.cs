using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RefillRequestTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC21;

// Test Case ID: TC-UC21-01
// Purpose / scenario: A patient's refill request arrives in the inbox of each clinician on their care team. One of them
//   opens it from the inbox, approves it on the order form, which opens with the clinic's item for the medication, and
//   signs the order: the request is approved with that order, the order is on the chart like any other, and the item
//   is done for the whole team.
// Type: Functional (successful flow)
// Requirements: FR-05, proposed FR-58
// Use case: UC 21, Answer a refill request
// Preconditions: Dr. Test and a colleague have the patient on their panels and are signed in on the on-site app. The
//   patient has asked, from the portal, to renew lisinopril 10 MG Oral Tablet, with a note.
// Inputs and system conditions: GET /inbox; GET /patients/{id}/refills/{request}; GET and POST
//   /patients/{id}/orders/new?type=medication&refill={request}; GET /patients/{id}; on the on-site app.
// Test data: the note "Synthetic: I have three days left."; the order's dose, route and frequency "50 mg", "By mouth",
//   "Once a day".
// Steps:
//   1. As Dr. Test, open the inbox, and follow "Review the request".
//   2. Follow "Approve request…", and sign the order.
//   3. Open the chart; as the colleague, open the inbox.
// Expected result: Step 1: the inbox lists a "Refill request" item, "A patient asked to renew a medication", with the
//   patient's name, a link to the request and no "Mark done"; the request's page shows the medication asked for and as
//   the chart has it, who asked, the note, and the link to approve; it is one recorded read of the patient. Step 2: the
//   order form says what the order approves and opens with the item "lisinopril 10 MG Oral Tablet", the medication's own
//   product, which can't be changed there; signing goes back to the
//   request's page, which says "Approved." and names Dr. Test and the item ordered. Step 3: the chart lists the order
//   under Active orders, signed by Dr. Test; the colleague's inbox has no item for it.
// Creator: Tahseen Arnan
public class TC_UC21_01_SuccessfulFlow(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_care_team_clinician_approves_a_request_from_the_inbox_through_the_order_form()
    {
        var (clinician, session, patient) = await PortalPatientAsync(apps, "Renewal");
        var colleague = await apps.CreateStaffAsync("Dr. Colleague", StaffRoles.Clinician);
        await apps.AssignAsync(colleague, patient.Patient);
        await MedicationAsync(apps, patient.Patient);
        using (var asked = await AskAsync(patient.Client, Lisinopril, "Synthetic: I have three days left."))
        {
            Assert.Equal(PortalPath, PathOf(asked));
        }

        var request = await RequestIdAsync(apps, patient.Patient);

        var inbox = await session.Client.GetStringAsync("/inbox", Cancel);
        var row = inbox[inbox.IndexOf("data-inbox-item=\"refill-request\"", StringComparison.Ordinal)..];
        row = row[..row.IndexOf("</tr>", StringComparison.Ordinal)];
        Assert.Contains("Refill request", Words(row));
        Assert.Contains("A patient asked to renew a medication", Words(row));
        Assert.Contains($"href=\"{ReviewPath(patient.Patient, request)}\"", row);
        Assert.Contains("Review the request", Words(row));
        Assert.DoesNotContain("Mark done", row);

        var reads = await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient.Patient);
        var review = await session.Client.GetStringAsync(ReviewPath(patient.Patient, request), Cancel);
        var words = Said(review);
        Assert.Equal(reads + 1, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient.Patient));
        Assert.Contains($"Asked for {Lisinopril}", words);
        Assert.Contains($"On the chart now {Lisinopril}, 1 tablet by mouth daily · Active", words);
        Assert.Contains("Synthetic Renewal", words);
        Assert.Contains("Synthetic: I have three days left.", words);
        Assert.Contains($"href=\"{ApprovePath(patient.Patient, request).Replace("&", "&amp;", StringComparison.Ordinal)}\"", review);

        var form = await session.Client.GetStringAsync(ApprovePath(patient.Patient, request), Cancel);
        Assert.Contains("Refill request. Signing approves the request for", Said(form));
        Assert.Contains("name=\"Item\" value=\"lisinopril 10 MG Oral Tablet\"", form);
        Assert.Contains("This order renews the same medication, lisinopril 10 MG Oral Tablet.", Said(form));
        Assert.Matches("id=\"order-item\"[^>]*readonly", form);
        using var approved = await ApproveAsync(session.Client, patient.Patient, request);
        var answered = Said(await ReadAsync(approved));
        Assert.Equal(ReviewPath(patient.Patient, request), PathOf(approved));
        Assert.Contains("Approved. The order is signed, and the patient can see what was ordered.", answered);
        Assert.Contains("Approved by Dr. Test", answered);
        Assert.Contains("Ordered lisinopril 10 MG Oral Tablet.", answered);

        Assert.Equal([$"completed|{Lisinopril}|Synthetic: I have three days left.|{patient.Account}|lisinopril 10 MG Oral Tablet|-|{clinician.Id}"],
            await RequestsAsync(apps, patient.Patient));
        Assert.Equal([$"active|{clinician.Id}"], await ProxyTests.QueryAsync(apps,
            $"SELECT o.status || '|' || o.signer_id FROM ehr.orders o JOIN ehr.refill_requests r ON r.order_id = o.id WHERE r.id = '{request}' AND o.patient_id = @patient",
            patient.Patient));
        var chart = Said(await session.Client.GetStringAsync($"/patients/{patient.Patient}", Cancel));
        Assert.Contains("lisinopril 10 MG Oral Tablet (Medication) Dose: 50 mg", chart);
        Assert.All(await ItemsAsync(apps, patient.Patient, request), item => Assert.EndsWith(" true", item));
        var colleagues = await SignInAsync(apps.OnPrem, colleague);
        Assert.DoesNotContain("A patient asked to renew a medication", Words(await colleagues.Client.GetStringAsync("/inbox", Cancel)));
    }
}
