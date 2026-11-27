using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.EmergencyAccessFlows;
using static Ehr.Web.Tests.InboxTests;

namespace Ehr.Web.Tests.TestCases.UC15;

// Test Case ID: TC-UC15-09
// Purpose / scenario: An emergency access to review is done by reviewing it, for every administrator, not by "Mark
//   done" (the expansion plan's step 18): the item has no such button, the database refuses the mark sent anyway, and
//   another administrator's review moves it under Done.
// Type: Functional (alternative flow)
// Requirements: proposed FR-41, proposed FR-64
// Use case: UC 15, Work through the inbox
// Preconditions: Dr. Inboxed Access used emergency access, now ended; the administrators Inbox Holder and Inbox
//   Reviewer each have its item.
// Inputs and system conditions: GET /inbox as Inbox Holder; POST /inbox?handler=Done for the item; Inbox Reviewer's
//   review; GET /inbox and /inbox?done=true.
// Test data: Note "Synthetic: checked the rota, the clinician was covering the ward."
// Steps:
//   1. As Inbox Holder, open the inbox; send "Mark done" for the item anyway.
//   2. As Inbox Reviewer, review the access.
//   3. As Inbox Holder, open the inbox and its Done view.
// Expected result: Step 1: the item, "Review the access" and no "Mark done"; the mark is refused, "Nothing to mark.", the
//   item still waiting. Step 2: saved. Step 3: "Nothing needs your action."; under Done, the item.
// Creator: Tahseen Arnan
public class TC_UC15_09_EmergencyAccessDoneByReviewing(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_emergency_access_is_done_by_reviewing_it_not_from_the_list()
    {
        var holderAccount = await apps.CreateStaffAsync("Inbox Holder", StaffRoles.Admin);
        var reviewerAccount = await apps.CreateStaffAsync("Inbox Reviewer", StaffRoles.Admin);
        var access = await GrantAsync(apps, "Dr. Inboxed Access");
        await EndAsync(apps, access.Id);
        var holder = await SignInAsync(apps.OnPrem, holderAccount);
        var item = Guid.Parse((await QueryAsync(apps,
            "SELECT id::text FROM ehr.inbox_items WHERE recipient_id = @r AND source_id = @a", ("r", holderAccount.Id), ("a", access.Id))).Single());

        // 1.
        var inbox = await holder.Client.GetStringAsync("/inbox", Cancel);
        Assert.Contains("Review the access", inbox);
        Assert.DoesNotContain("Mark done", inbox);
        using (var refused = await MarkDoneAsync(holder.Client, item, Token(inbox)))
        {
            Assert.Contains("Nothing to mark.", await ReadAsync(refused));
        }

        Assert.False(await IsDoneAsync(apps, item));

        // 2.
        var reviewer = await SignInAsync(apps.OnPrem, reviewerAccount);
        using (var reviewed = await ReviewAsync(reviewer.Client, access.Id, EmergencyAccessOutcomes.Appropriate, Note))
        {
            await ReadAsync(reviewed);
        }

        // 3.
        Assert.Contains("Nothing needs your action.", await holder.Client.GetStringAsync("/inbox", Cancel));
        Assert.Contains("A clinician used emergency access to a chart", await holder.Client.GetStringAsync("/inbox?done=true", Cancel));
        Assert.True(await IsDoneAsync(apps, item));
    }
}
