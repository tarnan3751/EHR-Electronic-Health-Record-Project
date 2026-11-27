using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.EmergencyAccessFlows;

namespace Ehr.Web.Tests.TestCases.UC12;

// Test Case ID: TC-UC12-08
// Purpose / scenario: The audit page marks each emergency access "Review", tinted, until an administrator reviews it,
//   then "Reviewed", and "Not appropriate" when it was found so; either way with the way to its review, and the reason
//   still shown (the expansion plan's step 18).
// Type: Functional (alternative flow)
// Requirements: FR-06, FR-32, proposed FR-64
// Use case: UC 12, Open a chart through emergency access
// Preconditions: Dr. Audited Access used emergency access, now ended; the administrator Tag Reader is signed in.
// Inputs and system conditions: GET /admin/audit?Patient={id}&Flagged=true before and after the review.
// Test data: Reason "Synthetic: covering the ward tonight, asked by the charge nurse."
// Steps:
//   1. Open the patient's emergency accesses on the audit page.
//   2. Review the access as not appropriate; open the page again.
// Expected result: Step 1: the access is tinted and tagged "Review", with "Open the review" and the reason. Step 2: it is
//   untinted and tagged "Reviewed" and "Not appropriate", with "Open the review" and the reason.
// Creator: Tahseen Arnan
public class TC_UC12_08_ReviewedOnTheAuditPage(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_audit_page_says_whether_each_emergency_access_was_reviewed()
    {
        var access = await GrantAsync(apps, "Dr. Audited Access");
        await EndAsync(apps, access.Id);
        var admin = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Tag Reader", StaffRoles.Admin));
        var link = $"<a data-review-link href=\"/admin/emergency-access/{access.Id}\">Open the review</a>";

        // 1.
        var waiting = await admin.Client.GetStringAsync($"/admin/audit?Patient={access.Patient}&Flagged=true", Cancel);
        Assert.Contains("<tr class=\"sc-table__flagged\">", waiting);
        Assert.Contains("Emergency access <span class=\"sc-tag sc-tag--danger\">Review</span>", waiting);
        Assert.Contains(link, waiting);
        Assert.Contains("Synthetic: covering the ward tonight, asked by the charge nurse.", waiting);

        // 2.
        using (var reviewed = await ReviewAsync(admin.Client, access.Id, EmergencyAccessOutcomes.NotAppropriate, Note))
        {
            await ReadAsync(reviewed);
        }

        var done = await admin.Client.GetStringAsync($"/admin/audit?Patient={access.Patient}&Flagged=true", Cancel);
        Assert.DoesNotContain("sc-table__flagged", done);
        Assert.Contains("Emergency access <span class=\"sc-tag\">Reviewed</span> <span class=\"sc-tag sc-tag--danger\">Not appropriate</span>", done);
        Assert.Contains(link, done);
        Assert.Contains("Synthetic: covering the ward tonight, asked by the charge nurse.", done);
    }
}
