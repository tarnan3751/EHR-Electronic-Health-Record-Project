using Xunit;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.ResultTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-12
// Purpose / scenario: A result filed at the clinic against an order reaches the patient's portal as results always
//   have: released at once, with its value, its reference range and whether it is inside it, whether or not a
//   clinician has acknowledged it yet. Another patient never sees it.
// Type: Functional (successful flow, continued)
// Requirements: FR-16, FR-30, FR-31, proposed FR-45
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: A clinician has signed a complete blood count for a patient on their panel. The patient, and a
//   second patient, have portal accounts and are signed in.
// Inputs and system conditions: POST /patients/{id}/orders/{order}/results as the clinician; GET /portal/results as
//   each patient.
// Test data: Hemoglobin 9.1 g/dL (reference 12.0 to 17.5).
// Steps:
//   1. As the patient, open "Results" before anything is filed.
//   2. As the clinician, file the result. Don't acknowledge it.
//   3. As the patient, open "Results". As the second patient, open "Results".
// Expected result: Step 1: no results. Step 3: the patient sees "Hemoglobin", with its full name "Hemoglobin
//   [Mass/volume] in Blood" under it, 9.1 g/dL and the range 12 to 17.5; the second patient sees no results.
// Creator: Tahseen Arnan
public class TC_UC6_12_FiledResultInPortal(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_result_filed_against_an_order_shows_in_the_patients_portal_and_nobody_elses()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        var order = await SignedAsync(session.Client, patient, "lab", "Complete blood count (CBC)", Cbc);
        var portal = await PortalResultsTests.SignInPatientAsync(apps, apps.OnPrem, patient);
        var other = await PortalResultsTests.SignInPatientAsync(apps, apps.OnPrem, await apps.InsertPatientAsync("Synthetic", "Bystander"));
        Assert.Contains("No test results yet.", await portal.Client.GetStringAsync("/portal/results", Cancel));

        using var filed = await FileAsync(apps, session.Client, patient, order, (Hemoglobin, "9.1"));
        Assert.Equal([$"{Hemoglobin} 9.1 low"], await FiledAsync(apps, patient));

        var results = await portal.Client.GetStringAsync("/portal/results", Cancel);
        Assert.Contains("<strong>Hemoglobin</strong>", results);
        Assert.Contains("Full name: Hemoglobin [Mass/volume] in Blood", results);
        Assert.Contains("9.1", results);
        Assert.Contains("12", results);
        Assert.Contains("17.5", results);
        Assert.Contains("No test results yet.", await other.Client.GetStringAsync("/portal/results", Cancel));
    }
}
