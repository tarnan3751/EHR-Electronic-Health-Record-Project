using System.Globalization;
using Ehr.Lab.Simulator;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.FhirRequests;
using static Ehr.Web.Tests.LabTests;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.ResultTests;

namespace Ehr.Web.Tests.TestCases.UC20;

// Test Case ID: TC-UC20-01
// Purpose / scenario: A lab order signed by a clinician gets its results from the lab with nobody typing them. The
//   simulated lab asks for new lab orders, is handed this one, waits its delay, and posts results that fit the
//   patient's diagnoses; the clinic files them as it files a clinician's: the order is resulted, the clinician who
//   signed it finds them in the inbox, the chart and the results page show them flagged, the patient sees them in the
//   portal, and the FHIR API serves them. Each step is in the audit log as the lab's.
// Type: Functional (successful flow)
// Requirements: proposed FR-54, with FR-05 and proposed FR-45
// Use case: UC 20, Receive results from a lab
// Preconditions: A lab is connected. A clinician on the on-site app has the patient on their panel; the patient has
//   type 2 diabetes (E11.9) on their list and a portal account.
// Inputs and system conditions: The order form; the simulated lab's code (src/Ehr.Lab.Simulator), in process, with
//   its default settings, calling the on-site app's /api/lab with the lab's key; the clinician's chart, inbox and
//   results page; the patient's portal results; GET /fhir/Observation?patient={id}&category=laboratory.
// Test data: A hemoglobin A1c order (SYN-LAB-005; 4.0 to 5.6 %, one decimal).
// Steps:
//   1. Sign the hemoglobin A1c order, and open its results page.
//   2. Run one round of the lab at the moment the order was signed, then one a minute later.
//   3. Open the chart, the inbox and the results page; sign in to the portal as the patient and open the results; read
//      the patient's laboratory results through the FHIR API.
//   4. Read the audit events about the patient whose source is the lab.
// Expected result: Step 1: the page says "Lab connected.". Step 2: the first round is handed the order and files
//   nothing yet; the second files it. The value is the one the simulator's rules give this order with E11.9, above 5.6,
//   collected a quarter of the way into its delay. Step 3: the order is resulted and listed as abnormal, to acknowledge;
//   the inbox has "Results are in" for it, abnormal; the page shows the value with "High" and no form; the portal and
//   the API show it too. Step 4: sent_to_lab for the order, then the result's insert, the order's update and the inbox
//   item's insert, each with no account.
// Creator: Tahseen Arnan
public class TC_UC20_01_SuccessfulFlow(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_lab_order_gets_its_results_from_the_lab_and_everyone_who_should_sees_them()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        await apps.InsertConditionAsync(patient, "Type 2 diabetes mellitus without complications", code: "E11.9");
        var since = await WatermarkAsync(apps);

        // Step 1.
        var order = await SignedAsync(session.Client, patient, "lab", "Hemoglobin A1c", Cbc);
        await SettledAsync(apps, patient);
        Assert.Contains("<span class=\"sc-notice__state\">Lab connected.</span>", await session.Client.GetStringAsync(Path(patient, order), Cancel));
        var signed = DateTimeOffset.Parse(
            (await ProxyTests.QueryAsync(apps, "SELECT to_char(signed_at AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') FROM ehr.orders WHERE patient_id = @patient", patient)).Single(),
            CultureInfo.InvariantCulture);

        // Step 2.
        var log = new List<string>();
        var lab = new LabSimulator(Client(apps.OnPrem), LabSettings.Default, log.Add, since);
        Assert.Equal(new LabRound(Handed: 1, Filed: 0, Dropped: 0, Waiting: 1), await lab.RunOnceAsync(signed, Cancel));
        Assert.Equal(new LabRound(Handed: 0, Filed: 1, Dropped: 0, Waiting: 0), await lab.RunOnceAsync(signed.AddMinutes(1), Cancel));
        Assert.Equal([$"Order {order}: 1 test, results due", $"Order {order}: results filed."], log.Select(line => line.Split(" 2", 2)[0]));

        var plan = SimulatedResults.For(new LabOrder(order, signed, [new LabTest("4548-4", "%", 4.0m, 5.6m, 1)], ["E11.9"]), LabSettings.Default);
        Assert.True(Assert.Single(plan.Results).Value > 5.6m);
        var value = plan.Results[0].Value.ToString("0.####", CultureInfo.InvariantCulture);
        Assert.Equal([$"4548-4 {value} high"], await FiledAsync(apps, patient));
        var collected = DateTimeOffset.Parse((await ProxyTests.QueryAsync(apps,
            "SELECT to_char(effective_at AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') FROM ehr.observations WHERE patient_id = @patient AND order_id IS NOT NULL",
            patient)).Single(), CultureInfo.InvariantCulture);
        Assert.Equal(plan.CollectedAt, collected, TimeSpan.FromMicroseconds(1));
        Assert.Equal("completed", await StatusAsync(apps, patient));

        // Step 3.
        var chart = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.Matches($"(?s)<li data-resulted=\"to-acknowledge\">\\s*<a href=\"{Path(patient, order)}\">Hemoglobin A1c</a>\\s*<span class=\"sc-tag sc-tag--warning\">Abnormal</span>", chart);
        var inbox = await session.Client.GetStringAsync("/inbox", Cancel);
        Assert.Contains("<span class=\"sc-tag\">Result to acknowledge</span> Results are in", inbox);
        Assert.Contains("data-inbox-abnormal>Abnormal</span>", inbox);
        Assert.Matches($"<a [^>]*data-inbox-source[^>]*href=\"{Path(patient, order)}\"[^>]*>Open the results</a>", inbox);
        var page = await session.Client.GetStringAsync(Path(patient, order), Cancel);
        Assert.Matches($"(?s)data-result=\"abnormal\".*?{value} %.*?<span class=\"sc-tag sc-tag--warning\">High</span>", page);
        Assert.DoesNotContain("Lab connected", page);
        Assert.DoesNotContain("data-file-results", page);

        var (portal, _) = await PortalResultsTests.SignInPatientAsync(apps, apps.OnPrem, patient);
        Assert.Matches("(?s)<strong>Hemoglobin A1c/Hemoglobin.total in Blood</strong>.*?<span class=\"sc-tag sc-tag--warning\">High</span>",
            await portal.GetStringAsync("/portal/results", Cancel));

        var fhir = await GetAsync(session.Client, $"/fhir/Observation?patient={patient}&category=laboratory");
        Assert.Contains("\"code\":\"4548-4\"", fhir.Json);
        Assert.Contains($"\"value\":{value}", fhir.Json);

        // Step 4.
        Assert.Equal(
            ["sent_to_lab orders - lab", "insert observations - lab", "update orders - lab", "insert inbox_items - lab"],
            await LabEventsAsync(apps, patient));
    }
}
