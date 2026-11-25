using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Data.Inbox;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.InboxTests;

namespace Ehr.Web.Tests.TestCases.UC15;

// Test Case ID: TC-UC15-02
// Purpose / scenario: An empty inbox says so and shows no count. And an admin has an inbox too: their items name a
//   patient by ID only, never by name, because admins have no access to patient data.
// Type: Functional (alternative flow)
// Requirements: proposed FR-41
// Use case: UC 15, Work through the inbox
// Preconditions: A clinician with nothing in their inbox, and an admin with one item about a patient, are signed in
//   on the on-site app.
// Inputs and system conditions: GET /inbox and GET /admin/staff on the on-site app; POST /inbox?handler=Done.
// Test data: Clinician "Dr. Inbox Empty". Admin "The Inbox Admin", with an item of kind emergency access to review:
//   "Synthetic: emergency access to review". Patient Synthetic Inboxed.
// Steps:
//   1. As the clinician, open the inbox, its "Done" view, and the patients page.
//   2. As the admin, open the staff page, then the inbox.
//   3. As the admin, choose "Mark done".
// Expected result: Step 1: "Nothing needs your action.", "Nothing done yet.", and an Inbox link with no count. Step 2:
//   the admin's app bar shows Inbox with 1; the item shows the patient's ID, not their name or MRN, and no link to a
//   chart; no read of the patient is recorded. Step 3: the item is done and the inbox is empty.
// Creator: Tahseen Arnan (expansion step 3); reviewed and run by Samir Mukshe, approving PR 2
public class TC_UC15_02_AlternativeFlow(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_empty_inbox_says_so_and_an_admin_sees_patients_by_id_only()
    {
        var (_, patient, session) = await ClinicianAsync(apps, "Dr. Inbox Empty");
        Assert.Contains("Nothing needs your action.", await session.Client.GetStringAsync("/inbox", Cancel));
        Assert.Contains("Nothing done yet.", await session.Client.GetStringAsync("/inbox?done=true", Cancel));
        var home = await session.Client.GetStringAsync("/patients", Cancel);
        Assert.Matches(@">\s*Inbox\s*</a>", home);
        Assert.DoesNotContain("data-inbox-count", home);

        var account = await apps.CreateStaffAsync("The Inbox Admin", StaffRoles.Admin);
        var item = await AddAsync(apps, account.Id, "Synthetic: emergency access to review", patient, InboxItemKind.EmergencyAccessReview);
        var admin = await SignInAsync(apps.OnPrem, account);
        Assert.Matches(@"Inbox\s*,\s*1\s*to do", Words(await admin.Client.GetStringAsync("/admin/staff", Cancel)));

        var inbox = await admin.Client.GetStringAsync("/inbox", Cancel);
        Assert.Contains("<span class=\"sc-tag\">Emergency access to review</span> Synthetic: emergency access to review", inbox);
        Assert.Contains($"<td class=\"sc-table__id\">{patient}</td>", inbox);
        Assert.DoesNotContain("Inboxed", inbox);
        Assert.DoesNotContain($"/patients/{patient}", inbox);
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.Read, account.Id, patient));

        using var done = await MarkDoneAsync(admin.Client, item);
        Assert.Contains("Nothing needs your action.", await ReadAsync(done));
        Assert.True(await IsDoneAsync(apps, item));
    }
}
