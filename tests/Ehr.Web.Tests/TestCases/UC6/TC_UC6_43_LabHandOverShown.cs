using Xunit;
using static Ehr.Web.Tests.LabTests;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.RecordViewTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-43
// Purpose / scenario: A lab order handed to the lab is listed as the lab's view, "The lab", "Lab order", for the patient
//   and for someone they share their record with, whatever that access includes; nothing of the order itself is on the
//   page.
// Type: Functional (alternative flow: the lab's hand-over)
// Requirements: proposed FR-57, proposed FR-54
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: A lab is connected. The patient has a confirmed portal account, signed in on the on-site app, and has
//   given another patient of the clinic access to their record without sensitive items; that proxy is looking at it.
//   Dr. Test has the patient on their panel.
// Inputs and system conditions: Dr. Test signs a lab order for the patient; the lab asks for new lab orders with its
//   key; the patient and the proxy GET /portal/views; on the on-site app.
// Test data: The lab order "Hemoglobin A1c" with the indication "Synthetic fatigue".
// Steps:
//   1. Dr. Test signs the order, and the lab is handed it.
//   2. The patient, then the proxy, opens Who viewed the record.
// Expected result: Step 1: the hand-over names the order. Step 2: each page has one line "The lab", "Lab order", not
//   marked; neither page names the order's item or its indication.
// Creator: Tahseen Arnan
public class TC_UC6_43_LabHandOverShown(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task The_labs_hand_over_is_listed_as_the_labs_view_and_nothing_of_the_order()
    {
        var (_, session, patient) = await ViewedPatientAsync(apps, "Labbed");
        var (proxy, _) = await VisitSummaryTests.ProxyForAsync(apps, patient, "Labproxy", sensitive: false);
        var since = await WatermarkAsync(apps);

        // Step 1.
        var order = await SignedAsync(session.Client, patient.Patient, "lab", "Hemoglobin A1c", Cbc);
        await SettledAsync(apps, patient.Patient);
        var (handed, body) = await HandOverAsync(LabTests.Client(apps.OnPrem), since);
        using (handed)
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, handed.StatusCode);
            Assert.Contains(order.ToString(), body.GetRawText());
        }

        // Step 2.
        foreach (var client in new[] { patient.Client, proxy.Client })
        {
            var html = await PageAsync(client);
            Assert.Single(Lines(html), line => line.StartsWith("lab | The lab | Lab order | ", StringComparison.Ordinal));
            Assert.DoesNotContain("Hemoglobin", html);
            Assert.DoesNotContain("fatigue", html);
        }
    }
}
