using Xunit;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.ResultTests;

namespace Ehr.Web.Tests.TestCases.UC15;

// Test Case ID: TC-UC15-07
// Purpose / scenario: In the inbox, results with a value outside its reference range come before everything else
//   still to do, whatever arrived first; and a result is not ticked off from the list, only acknowledged on its page.
// Type: Functional (alternative flow: a result to acknowledge)
// Requirements: proposed FR-41, proposed FR-45
// Use case: UC 15, Work through the inbox
// Preconditions: A clinician has signed a lipid panel and then a complete blood count for a patient on their panel.
// Inputs and system conditions: POST /patients/{id}/orders/{order}/results twice; GET /inbox.
// Test data: Lipid panel: total cholesterol 180 (inside its range), filed first. Complete blood count: hemoglobin 9.1
//   (below its range), filed second.
// Steps:
//   1. File the lipid panel's result, then the blood count's.
//   2. Open the inbox.
//   3. Post "Mark done" for one of the items, as a crafted request.
// Expected result: Two "Result to acknowledge" items. The blood count's, tagged "Abnormal", is listed first although
//   it arrived second; the lipid panel's has no tag. Neither has a "Mark done" button; each has "Open the results".
//   The app bar counts 2. Step 3: nothing is marked done.
// Creator: Tahseen Arnan
public class TC_UC15_07_AbnormalResultsFirst(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Abnormal_results_come_first_in_the_inbox_and_are_done_only_by_acknowledging()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        var lipids = await SignedAsync(session.Client, patient, "lab", "Lipid panel", Cbc);
        var count = await SignedAsync(session.Client, patient, "lab", "Complete blood count (CBC)", Cbc);
        // Total cholesterol, one of the lipid panel's tests (db/reference): 125 to 199.
        const string cholesterol = "2093-3";

        using var normal = await FileAsync(apps, session.Client, patient, lipids, (cholesterol, "180"));
        using var abnormal = await FileAsync(apps, session.Client, patient, count, (Hemoglobin, "9.1"));
        Assert.Equal(2, (await FiledAsync(apps, patient)).Count);

        var inbox = await session.Client.GetStringAsync("/inbox", Cancel);
        var first = inbox.IndexOf(Path(patient, count), StringComparison.Ordinal);
        var second = inbox.IndexOf(Path(patient, lipids), StringComparison.Ordinal);
        Assert.True(first > 0 && second > first, "The abnormal result is listed before the normal one that arrived first.");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(inbox, "data-inbox-abnormal"));
        Assert.True(inbox.IndexOf("data-inbox-abnormal", StringComparison.Ordinal) < second);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(inbox, ">Open the results</a>").Count);
        Assert.DoesNotContain("Mark done", inbox);
        Assert.Matches("data-inbox-count>.*?2", inbox);

        // Posting "Mark done" for one anyway changes nothing: the database refuses it.
        var item = (await MessageTests.RowsAsync(apps, patient, "id::text", "inbox_items", "created_at")).First();
        using var ticked = await Browser.PostFormAsync(session.Client, $"/inbox?handler=Done&id={item}", "/inbox");
        Assert.Equal(2, (await MessageTests.RowsAsync(apps, patient, "id::text", "inbox_items", "created_at", "done_at IS NULL")).Count);
    }
}
