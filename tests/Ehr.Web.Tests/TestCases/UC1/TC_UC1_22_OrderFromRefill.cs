using System.Net;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RefillRequestTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC1;

// Test Case ID: TC-UC1-22
// Purpose / scenario: The order form approves a refill request (step 13d) as an ordinary medication order: it opens
//   with the clinic's item for the medication asked for and offers no other type, says what it approves, and refuses
//   another drug; the order it signs is one like any other, on the chart, in the write audit, and approving at another
//   strength is allowed. Showing a sensitive medication's request is a recorded view of sensitive items. A request that
//   isn't this patient's sends the form back to the chart.
// Type: Functional (alternative flow: approving a refill request)
// Requirements: FR-05, proposed FR-37, FR-47 and FR-58
// Use case: UC 1, Place an order
// Preconditions: Dr. Test has two patients on their panel, signed in on the on-site app. The first has asked to renew
//   lisinopril, and sertraline, which is marked sensitive.
// Inputs and system conditions: GET and POST /patients/{id}/orders/new?type=medication&refill={request}, on the
//   on-site app.
// Test data: Atorvastatin 20 mg tablet; lisinopril 20 MG Oral Tablet; Sertraline 50 mg tablet.
// Steps:
//   1. Open the form for the lisinopril request; then for it under the second patient.
//   2. Sign with Atorvastatin 20 mg tablet, then with lisinopril 20 MG Oral Tablet.
//   3. Open the form for the sertraline request.
// Expected result: Step 1: the form has no type tabs, the item "Lisinopril 10 mg tablet", and "Approves:"; opening one
//   of this patient's requests under the other patient goes to that patient's chart. Step 2: the first is refused
//   (422, the item marked) with nothing saved; the second signs an active order for lisinopril 20 MG Oral Tablet, signed
//   by Dr. Test, with its insert in the write audit, listed on the chart; the request is approved with it. Step 3: the
//   form is a recorded view of the patient's sensitive items.
// Creator: Tahseen Arnan
public class TC_UC1_22_OrderFromRefill(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_order_form_approves_a_refill_with_an_ordinary_order_for_the_same_medication()
    {
        var (clinician, session, patient) = await PortalPatientAsync(apps, "Refilled");
        var second = await apps.InsertPatientAsync("Synthetic", "Refilledsecond");
        await apps.AssignAsync(clinician, second);
        await MedicationAsync(apps, patient.Patient);
        await MedicationAsync(apps, patient.Patient, "sertraline 50 MG Oral Tablet", "312941", sensitive: true);
        using (var asked = await AskAsync(patient.Client, Lisinopril)) { Assert.Equal(PortalPath, PathOf(asked)); }
        var lisinopril = await RequestIdAsync(apps, patient.Patient);
        using (var asked = await AskAsync(patient.Client, "sertraline 50 MG Oral Tablet", sensitive: true)) { Assert.Equal(PortalPath, PathOf(asked)); }
        var sertraline = await RequestIdAsync(apps, patient.Patient);

        var form = await session.Client.GetStringAsync(ApprovePath(patient.Patient, lisinopril), Cancel);
        Assert.DoesNotContain("aria-label=\"Type of order\"", form);
        Assert.Contains("name=\"Item\" value=\"Lisinopril 10 mg tablet\"", form);
        Assert.Contains("Approves: the refill request for lisinopril 10 MG Oral Tablet", Said(form));
        using (var elsewhere = await session.Client.GetAsync(ApprovePath(second, lisinopril), Cancel))
        {
            Assert.Equal($"/patients/{second}", PathOf(elsewhere));
        }

        using (var refused = await ApproveAsync(session.Client, patient.Patient, lisinopril, "Atorvastatin 20 mg tablet"))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            var html = await refused.Content.ReadAsStringAsync(Cancel);
            Assert.Contains("A refill renews the same medication, at any strength.", Said(html));
            Assert.Contains("id=\"order-item\"", html);
        }

        Assert.Equal(["0"], await ProxyTests.QueryAsync(apps, "SELECT count(*)::text FROM ehr.orders WHERE patient_id = @patient", patient.Patient));

        using (var approved = await ApproveAsync(session.Client, patient.Patient, lisinopril, "lisinopril 20 MG Oral Tablet"))
        {
            Assert.Contains("Approved:", Said(await ReadAsync(approved)));
        }

        var order = (await ProxyTests.QueryAsync(apps,
            $"SELECT o.id || '|' || o.status || '|' || o.signer_id || '|' || o.catalog_code FROM ehr.orders o JOIN ehr.refill_requests r ON r.order_id = o.id WHERE r.id = '{lisinopril}' AND o.patient_id = @patient",
            patient.Patient)).Single().Split('|');
        Assert.Equal(["active", clinician.Id.ToString(), "314077"], order[1..]);
        Assert.Equal(["insert"], await ProxyTests.QueryAsync(apps,
            $"SELECT action FROM ehr.audit_events WHERE patient_id = @patient AND table_name = 'orders' AND row_id = '{order[0]}'", patient.Patient));
        Assert.Contains("lisinopril 20 MG Oral Tablet", Words(await session.Client.GetStringAsync($"/patients/{patient.Patient}", Cancel)));

        var sensitiveViews = await apps.AuditEventsAsync(AuditActions.ReadSensitive, clinician.Id, patient.Patient);
        Assert.Contains("Sensitive", Said(await session.Client.GetStringAsync(ApprovePath(patient.Patient, sertraline), Cancel)));
        Assert.Equal(sensitiveViews + 1, await apps.AuditEventsAsync(AuditActions.ReadSensitive, clinician.Id, patient.Patient));
    }
}
