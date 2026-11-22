using Ehr.Lab.Simulator;
using Xunit;
using static Ehr.Web.Tests.LabTests;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.ResultTests;

namespace Ehr.Web.Tests.TestCases.UC20;

// Test Case ID: TC-UC20-02
// Purpose / scenario: The other ways results come from a lab. An order a clinician has partly filed by hand is handed
//   over with only the tests still waiting, and the lab files the rest beside the clinician's. Two labs running at once
//   are each handed the same orders and post the same results, which are filed once. A lab started again asks from the
//   beginning and is handed only what still waits.
// Type: Functional (alternative flow)
// Requirements: proposed FR-54, with proposed FR-45
// Use case: UC 20, Receive results from a lab
// Preconditions: A lab is connected. A clinician on the on-site app has the patient on their panel; the patient has
//   hypothyroidism (E03.9) and vitamin D deficiency (E55.9) on their list.
// Inputs and system conditions: The order form and the results page; two copies of the simulated lab's code, in
//   process, with their default settings, run side by side; then a third, started from the beginning.
// Test data: A complete blood count, with hemoglobin 13.1 g/dL filed by hand; a TSH (0.4 to 4.0 m[IU]/L); then a
//   vitamin D (30 to 100 ng/mL).
// Steps:
//   1. Sign the blood count and file its hemoglobin by hand; sign the TSH.
//   2. Run a round of each lab at the moment the TSH was signed, then a round of both at once a minute later.
//   3. Sign the vitamin D. Start a third lab, from the beginning, and run a round at that moment.
// Expected result: Step 2: each lab is handed both orders, the count with its three tests still waiting; then each
//   posts both and is answered 204 for both. The count has its four results once each, the hemoglobin still 13.1; the
//   TSH has one, above its range. Each order has one inbox item. Step 3: the third lab is handed the vitamin D alone.
//   Each order handed over is one sent_to_lab event for each lab it went to: five.
// Creator: Tahseen Arnan
public class TC_UC20_02_AlternativeFlow(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Part_filed_orders_two_labs_at_once_and_a_lab_started_again_each_file_every_result_once()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        await apps.InsertConditionAsync(patient, "Hypothyroidism, unspecified", code: "E03.9");
        await apps.InsertConditionAsync(patient, "Vitamin D deficiency, unspecified", code: "E55.9");
        var since = await WatermarkAsync(apps);

        // Step 1.
        var count = await SignedAsync(session.Client, patient, "lab", "Complete blood count (CBC)", Cbc);
        using (await FileAsync(apps, session.Client, patient, count, (Hemoglobin, "13.1")))
        {
        }

        var thyroid = await SignedAsync(session.Client, patient, "lab", "Thyroid-stimulating hormone (TSH)", Cbc);
        await SettledAsync(apps, patient);
        DateTimeOffset signed = await apps.Database.NowAsync();

        // Step 2.
        var (firstLog, secondLog) = (new List<string>(), new List<string>());
        var first = new LabSimulator(Client(apps.OnPrem), LabSettings.Default, firstLog.Add, since);
        var second = new LabSimulator(Client(apps.OnPrem), LabSettings.Default, secondLog.Add, since);
        var before = signed.AddMinutes(-1);
        Assert.Equal(new LabRound(2, 0, 0, 2), await first.RunOnceAsync(before, Cancel));
        Assert.Equal(new LabRound(2, 0, 0, 2), await second.RunOnceAsync(before, Cancel));
        Assert.Contains(firstLog, line => line.StartsWith($"Order {count}: 3 tests,", StringComparison.Ordinal));
        Assert.Contains(secondLog, line => line.StartsWith($"Order {thyroid}: 1 test,", StringComparison.Ordinal));

        var rounds = await Task.WhenAll(first.RunOnceAsync(signed.AddMinutes(1), Cancel), second.RunOnceAsync(signed.AddMinutes(1), Cancel));
        Assert.All(rounds, round => Assert.Equal(new LabRound(0, 2, 0, 0), round));

        var filed = await FiledAsync(apps, patient);
        Assert.Equal(["3016-3", "4544-3", "6690-2", Hemoglobin, "777-3"], filed.Select(row => row.Split(' ')[0]));
        Assert.Contains($"{Hemoglobin} 13.1 normal", filed);
        Assert.Contains(filed, row => row.StartsWith("3016-3 ", StringComparison.Ordinal) && row.EndsWith(" high", StringComparison.Ordinal));
        Assert.Equal(["1", "1"], await ProxyTests.QueryAsync(apps,
            "SELECT count(*)::text FROM ehr.orders o JOIN ehr.inbox_items i ON i.source_id = o.id WHERE o.patient_id = @patient GROUP BY o.id", patient));

        // Step 3.
        var vitaminD = await SignedAsync(session.Client, patient, "lab", "Vitamin D, 25-hydroxy", Cbc);
        await SettledAsync(apps, patient);
        var thirdLog = new List<string>();
        var third = new LabSimulator(Client(apps.OnPrem), LabSettings.Default, thirdLog.Add);
        Assert.Equal(new LabRound(1, 0, 0, 1), await third.RunOnceAsync((await apps.Database.NowAsync()).AddMinutes(-1), Cancel));
        Assert.StartsWith($"Order {vitaminD}: 1 test,", Assert.Single(thirdLog), StringComparison.Ordinal);
        Assert.Equal(5, await apps.AuditEventsAsync("sent_to_lab", patientId: patient));
    }
}
