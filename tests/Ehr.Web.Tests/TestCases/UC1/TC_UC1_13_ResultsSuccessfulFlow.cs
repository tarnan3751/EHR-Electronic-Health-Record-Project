using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.ResultTests;

namespace Ehr.Web.Tests.TestCases.UC1;

// Test Case ID: TC-UC1-13
// Purpose / scenario: A lab order's results come back. A clinician files them against the order; the order moves to
//   resulted; the abnormal one is flagged and listed first; the clinician who signed the order finds an item in their
//   inbox and acknowledges the results, which records who and when.
// Type: Functional (successful flow, continued: order, result, acknowledgment)
// Requirements: FR-05, proposed FR-45
// Use case: UC 1, Place an order
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel and has signed a
//   complete blood count for them.
// Inputs and system conditions: GET /patients/{id}; GET and POST /patients/{id}/orders/{order}/results and
//   ?handler=Acknowledge; GET /inbox.
// Test data: Platelets 250 (reference 150 to 400), hemoglobin 9.1 g/dL (reference 12.0 to 17.5).
// Steps:
//   1. On the chart, follow "Enter results" on the order. Type the two values and choose "File results".
//   2. Open the inbox.
//   3. Follow "Open the results" and choose "Acknowledge results".
//   4. Open the inbox and the chart.
// Expected result: Step 1: the page says "Saved."; hemoglobin is listed first with "Low" and its reference range,
//   platelets after it with no flag; "Abnormal. 1 of 2 results is outside the reference range"; the names, units and ranges are
//   the clinic's own; the order is completed. Step 2: one "Result to acknowledge" item, "Results are in", tagged
//   "Abnormal", with the patient's name and no value, and no "Mark done" button. Step 3: "Results acknowledged.", then
//   "Acknowledged by" the clinician with the time; the acknowledgment is in the write audit under their account. Step
//   4: the inbox is empty; the chart's Results section lists the order as abnormal and no longer "To acknowledge", and
//   it is no longer among the active orders.
// Creator: Tahseen Arnan
public class TC_UC1_13_ResultsSuccessfulFlow(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Results_are_filed_against_their_order_flagged_and_acknowledged_from_the_inbox()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        var order = await SignedAsync(session.Client, patient, "lab", "Complete blood count (CBC)", Cbc);
        var chart = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.Matches($"<a [^>]*data-enter-results[^>]*href=\"{Path(patient, order)}\"[^>]*>Enter results</a>", chart);
        Assert.Contains("No results yet.", chart);
        var empty = await session.Client.GetStringAsync(Path(patient, order), Cancel);
        Assert.Contains("No results yet.", empty);
        Assert.Contains("Hemoglobin [Mass/volume] in Blood <span>(g/dL; reference 12 to 17.5)</span>", empty);

        using var filed = await FileAsync(apps, session.Client, patient, order, (Platelets, "250"), (Hemoglobin, "9.1"));
        var page = await ReadAsync(filed);
        Assert.Contains("Saved.", page);
        Assert.Contains("Abnormal. 1 of 2 results is outside the reference range, listed first.", Words(page));
        Assert.True(page.IndexOf("Hemoglobin [Mass/volume]", StringComparison.Ordinal) < page.IndexOf("Platelets [#/volume]", StringComparison.Ordinal));
        Assert.Matches("(?s)data-result=\"abnormal\".*?9.1 g/dL.*?<span class=\"sc-tag sc-tag--warning\">Low</span>.*?12 to 17.5 g/dL", page);
        Assert.Matches("(?s)data-result=\"normal\".*?250 10\\*3/uL", page);
        Assert.Equal([$"{Hemoglobin} 9.1 low", $"{Platelets} 250 normal"], await FiledAsync(apps, patient));
        Assert.Equal("completed", await StatusAsync(apps, patient));

        var inbox = await session.Client.GetStringAsync("/inbox", Cancel);
        Assert.Contains("<span class=\"sc-tag\">Result to acknowledge</span> Results are in", inbox);
        Assert.Contains("data-inbox-abnormal>Abnormal</span>", inbox);
        Assert.Matches($"<a [^>]*data-inbox-source[^>]*href=\"{Path(patient, order)}\"[^>]*>Open the results</a>", inbox);
        Assert.Contains("Orderable, Synthetic", inbox);
        Assert.DoesNotContain("9.1", inbox);
        Assert.DoesNotContain("Mark done", inbox);

        using var acknowledged = await AcknowledgeAsync(session.Client, patient, order);
        var after = await ReadAsync(acknowledged);
        Assert.Contains("Results acknowledged.", after);
        Assert.Matches($"data-acknowledged>Acknowledged by Dr. Test, [A-Z][a-z]{{2}} \\d{{1,2}}, \\d{{4}} \\d{{2}}:\\d{{2}} UTC\\.", after);
        Assert.DoesNotContain("data-acknowledge>", after);
        Assert.Equal([$"{clinician.Id}"], await ProxyTests.QueryAsync(apps,
            "SELECT actor_id::text FROM ehr.audit_events WHERE table_name = 'result_acknowledgments' AND action = 'insert' AND patient_id = @patient", patient));

        Assert.Contains("Nothing needs your action.", await session.Client.GetStringAsync("/inbox", Cancel));
        chart = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.Matches($"(?s)<li data-resulted=\"acknowledged\">\\s*<a href=\"{Path(patient, order)}\">Complete blood count \\(CBC\\)</a>\\s*<span class=\"sc-tag sc-tag--warning\">Abnormal</span>", chart);
        Assert.DoesNotContain("To acknowledge", chart);
        Assert.DoesNotContain("data-enter-results", chart);
        Assert.True(await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient) >= 4);
    }
}
