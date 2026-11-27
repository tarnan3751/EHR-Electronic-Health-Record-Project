using Ehr.Data.Accounts;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RecordViewTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-41
// Purpose / scenario: A view made through emergency access is marked "Emergency access" on the patient's page, with a
//   notice saying what that means, and the reason the clinician gave is nowhere on it. A view by a clinician with the
//   patient on their panel is not marked, even inside an emergency-access window. Someone the patient shares their
//   record with sees the same only when the patient allowed them sensitive items; without them, they are shown no view
//   from outside the care team and no word of emergency access. The clinician is told so before giving the reason.
// Type: Functional (alternative flow: emergency access)
// Requirements: proposed FR-57, FR-19
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a confirmed portal account, signed in on the on-site app, and has given two other
//   patients of the clinic access, one with sensitive items and one without; each is looking at the record. Dr. Test has
//   the patient on their panel. Dr. Oak, a clinician, doesn't.
// Inputs and system conditions: Dr. Test opens the chart; Dr. Oak opens it, is shown the reason page, gives a reason
//   and reads it; Dr. Oak puts the patient on their panel and opens the chart again, still inside the emergency-access
//   window; the patient and both proxies GET /portal/views; on the on-site app.
// Test data: The reason "Synthetic reason: covering a colleague's patients overnight."
// Steps:
//   1. Dr. Test opens the chart.
//   2. Dr. Oak opens it, reads the reason page, gives the reason, and reads the chart.
//   3. Dr. Oak puts the patient on their panel and opens the chart again.
//   4. The patient, the proxy with sensitive items, and the proxy without, open Who viewed the record.
// Expected result: Step 2: the reason page says "The patient can see this access in the portal." Step 4: the patient's lines
//   from the care team are, newest first: Dr. Oak (step 3), not marked; Dr. Oak (step 2), marked "Emergency access";
//   Dr. Test, not marked. The notice "Emergency access. A clinician outside your care team opened the record and gave
//   a reason. The clinic reviews each one." is shown. The proxy with sensitive items has the same three
//   lines and the notice. The proxy without has Dr. Oak (step 3) and Dr. Test only, and no word "Emergency" on the page.
//   The reason is nowhere in any of the three pages.
// Creator: Tahseen Arnan
public class TC_UC6_41_EmergencyAccessMarked(WebApps apps) : IClassFixture<WebApps>
{
    const string Reason = "Synthetic reason: covering a colleague's patients overnight.";

    [Fact]
    public async Task A_view_through_emergency_access_is_marked_for_those_who_may_see_it_and_its_reason_is_not_shown()
    {
        var (_, session, patient) = await ViewedPatientAsync(apps, "Covered");
        var (allowed, _) = await VisitSummaryTests.ProxyForAsync(apps, patient, "Sensitiveallowed", sensitive: true);
        var (without, _) = await VisitSummaryTests.ProxyForAsync(apps, patient, "Sensitivewithout", sensitive: false);
        var chart = $"/patients/{patient.Patient}";

        // Step 1.
        using (var read = await session.Client.GetAsync(chart, Cancel))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, read.StatusCode);
        }

        // Step 2.
        var oak = await apps.CreateStaffAsync("Dr. Oak", StaffRoles.Clinician);
        var away = await SignInAsync(apps.OnPrem, oak);
        Assert.Contains("The patient can see this access in the portal.",
            Words(await away.Client.GetStringAsync(chart + "/reason", Cancel)));
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
        static List<string> Staff(string html) =>
            [.. Lines(html).Where(line => line.StartsWith("staff", StringComparison.Ordinal)).Select(line => string.Join(" | ", line.Split(" | ").Take(3)))];
        var html = await PageAsync(patient.Client);
        Assert.Equal(
            [
                "staff | Dr. Oak, clinician | Your chart, with the list of your visit notes",
                "staff | Dr. Oak, clinician Emergency access | Your chart, with the list of your visit notes",
                "staff | Dr. Test, clinician | Your chart, with the list of your visit notes",
            ], Staff(html));
        Assert.Contains("Emergency access. A clinician outside your care team opened the record and gave a reason. The clinic reviews each one.", Words(html));

        var allowedHtml = await PageAsync(allowed.Client);
        Assert.Equal(
            [
                "staff | Dr. Oak, clinician | The chart, with the list of visit notes",
                "staff | Dr. Oak, clinician Emergency access | The chart, with the list of visit notes",
                "staff | Dr. Test, clinician | The chart, with the list of visit notes",
            ], Staff(allowedHtml));
        Assert.Contains("Emergency access. A clinician outside the patient's care team opened the record", Words(allowedHtml));

        var withoutHtml = await PageAsync(without.Client);
        Assert.Equal(["staff | Dr. Oak, clinician | The chart, with the list of visit notes", "staff | Dr. Test, clinician | The chart, with the list of visit notes"], Staff(withoutHtml));
        Assert.DoesNotContain("Emergency", Words(withoutHtml), StringComparison.Ordinal);
        Assert.Contains("Each time the patient's care team, the lab or you saw the record", Words(withoutHtml));

        foreach (var page in new[] { html, allowedHtml, withoutHtml })
        {
            Assert.DoesNotContain("colleague", page, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("overnight", page, StringComparison.OrdinalIgnoreCase);
        }
    }
}
