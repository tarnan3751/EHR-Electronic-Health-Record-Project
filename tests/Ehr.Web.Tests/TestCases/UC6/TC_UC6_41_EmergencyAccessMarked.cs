using Ehr.Data.Accounts;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RecordViewTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-41
// Purpose / scenario: A view made through emergency access is marked "Emergency access" on the patient's page, with a
//   notice saying what that means, and the reason the clinician gave is nowhere on it. A view by a clinician with the
//   patient on their panel is not marked, even inside an emergency-access window.
// Type: Functional (alternative flow: emergency access)
// Requirements: proposed FR-57
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a confirmed portal account, signed in on the on-site app. Dr. Test has the patient on
//   their panel. Dr. Oak, a clinician, doesn't.
// Inputs and system conditions: Dr. Test opens the chart; Dr. Oak opens it, gives a reason and reads it; Dr. Oak puts
//   the patient on their panel and opens the chart again, still inside the emergency-access window; the patient GETs
//   /portal/views; on the on-site app.
// Test data: The reason "Synthetic reason: covering a colleague's patients overnight."
// Steps:
//   1. Dr. Test opens the chart.
//   2. Dr. Oak opens it, is asked for a reason, gives it, and reads the chart.
//   3. Dr. Oak puts the patient on their panel and opens the chart again.
//   4. The patient opens Who viewed my record.
// Expected result: The lines are, newest first: Dr. Oak (step 3), not marked; Dr. Oak (step 2), marked "Emergency
//   access"; Dr. Test, not marked; then the patient's own view. The notice "Emergency access: a clinician who wasn't on
//   your care team opened the record and gave the clinic a reason, which the clinic reviews." is shown. The reason is
//   nowhere in the markup.
// Creator: Tahseen Arnan
public class TC_UC6_41_EmergencyAccessMarked(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    const string Reason = "Synthetic reason: covering a colleague's patients overnight.";

    [Fact]
    public async Task A_view_through_emergency_access_is_marked_and_its_reason_is_not_shown()
    {
        var (_, session, patient) = await ViewedPatientAsync(apps, "Emergency");
        var chart = $"/patients/{patient.Patient}";

        // Step 1.
        using (var read = await session.Client.GetAsync(chart, Cancel))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, read.StatusCode);
        }

        // Step 2.
        var oak = await apps.CreateStaffAsync("Dr. Oak", StaffRoles.Clinician);
        var away = await SignInAsync(apps.OnPrem, oak);
        using (var opened = await PostFormAsync(away.Client, chart + "/reason", chart + "/reason", ("Reason", Reason)))
        {
            Assert.Equal(chart, PathOf(opened));
        }

        // Step 3.
        await AssignNowAsync(apps, oak, patient.Patient);
        using (var again = await away.Client.GetAsync(chart, Cancel))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, again.StatusCode);
        }

        // Step 4.
        var html = await PageAsync(patient.Client);
        var lines = Lines(html).Select(line => string.Join(" | ", line.Split(" | ").Take(3))).ToList();
        Assert.Equal(
            [
                "staff | Dr. Oak, clinician | Your record",
                "staff | Dr. Oak, clinician Emergency access | Your record",
                "staff | Dr. Test, clinician | Your record",
                "you | You | Your record",
            ], lines);
        Assert.Contains("Emergency access: a clinician who wasn't on your care team opened the record and gave the clinic a reason, which the clinic reviews.", Words(html));
        Assert.DoesNotContain("colleague", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("overnight", html, StringComparison.OrdinalIgnoreCase);
    }
}
