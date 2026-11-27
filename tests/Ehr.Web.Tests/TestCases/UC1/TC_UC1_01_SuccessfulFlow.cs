using System.Net;
using System.Text.Json;
using Ehr.Data.Audit;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC1;

// Test Case ID: TC-UC1-01
// Purpose / scenario: A clinician signs a lab order for a patient on their panel. The chart confirms the order and
//   lists it under active orders, and the audit row names the clinician and the time.
// Type: Functional (successful flow)
// Requirements: FR-05, FR-29, FR-32
// Use case: UC 1, Place an order
// Preconditions: The clinician is signed in on the on-site app, with password and authenticator code, and the patient
//   is on their panel. The patient has no active orders. The clinic's server is up.
// Inputs and system conditions: GET the chart, GET /patients/{id}/orders/new?type=lab, then POST the form as
//   js/orders.js sends it, on the on-site app (ehr_app role).
// Test data: Patient Synthetic Orderable, allergic to Penicillin (high risk, reaction "Hives"). Order type Lab, item
//   typed as "complete blood count (CBC)" (the catalog's "Complete blood count (CBC)", SYN-LAB-001), Reason for the
//   test "Synthetic fatigue".
// Steps:
//   1. Open the patient's chart: it offers "New order" and says "No active orders."
//   2. Open the lab order form: the patient's name and allergies are at the top, the catalog's items are suggested, and
//      the required field is marked.
//   3. Enter the item and the reason, and select "Sign order".
//   4. Follow the answer back to the chart.
//   5. Read the order and its audit row from the database.
// Expected result: The chart shows "Order signed." and lists, under "Active orders",
//   "Complete blood count (CBC) (Lab) Reason for the test: Synthetic fatigue"; "No active orders." is gone. The form's
//   banner and allergies are one recorded read. The order's signer is the clinician and its signing time is the
//   moment of signing; the audit row for the insert names the same clinician, at that time.
// Creator: Tahseen Arnan
public class TC_UC1_01_SuccessfulFlow(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_clinician_signs_an_order_and_the_chart_confirms_and_lists_it()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);

        var chart = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.Contains($"href=\"/patients/{patient}/orders/new\">New order <kbd class=\"sc-kbd\" aria-hidden=\"true\">Alt+Shift+O</kbd></a>", chart);
        Assert.Contains("data-new-order aria-keyshortcuts=\"Alt+Shift+O\"", chart);
        Assert.Contains("/_content/Ehr.Design/js/orders.", chart);
        Assert.DoesNotContain("accesskey", chart);
        Assert.Contains("No active orders.", chart);

        var reads = await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient);
        var form = await session.Client.GetStringAsync(FormPath(patient, "lab"), Cancel);
        Assert.Contains("<h1>New order</h1>", form);
        Assert.Contains("<strong>Penicillin</strong>: Hives", form);
        Assert.Contains("High risk", form);
        Assert.Contains("<option value=\"Complete blood count (CBC)\"></option>", form);
        Assert.Matches(@"<label for=""order-indication"">Reason for the test\s*<span>\(required\)</span>", form);
        Assert.Contains("name=\"Details[indication]\"", form);
        Assert.DoesNotContain("htmx.min.js", form);
        // The banner and allergies are one recorded read.
        Assert.Equal(reads + 1, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient));

        // By the database's clock, which stamps the times.
        DateTimeOffset before = (await apps.Database.NowAsync()).AddSeconds(-1);
        using var signed = await SignAsync(session.Client, patient, "lab", "complete blood count (CBC)", Cbc);
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        var url = JsonDocument.Parse(await signed.Content.ReadAsStringAsync(Cancel)).RootElement.GetProperty("url").GetString()!;

        var confirmed = await session.Client.GetStringAsync(url, Cancel);
        Assert.Contains("Order signed.", Words(confirmed));
        Assert.Contains("<h3>Active orders</h3>", confirmed);
        Assert.Contains("<strong>Complete blood count (CBC)</strong> (Lab)", confirmed);
        Assert.Contains("Reason for the test: Synthetic fatigue", confirmed);
        Assert.DoesNotContain("No active orders.", confirmed);

        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT o.signer_id, o.signed_at, e.actor_id, e.occurred_at
            FROM ehr.orders o JOIN ehr.audit_events e ON e.row_id = o.id AND e.action = 'insert' AND e.table_name = 'orders'
            WHERE o.patient_id = @patient
            """, db);
        command.Parameters.AddWithValue("patient", patient);
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        Assert.True(await reader.ReadAsync(Cancel));
        Assert.Equal(clinician.Id, reader.GetGuid(0));
        Assert.InRange(reader.GetFieldValue<DateTimeOffset>(1), before, (DateTimeOffset)(await apps.Database.NowAsync()).AddSeconds(1));
        Assert.Equal(clinician.Id, reader.GetGuid(2));
        Assert.InRange(reader.GetFieldValue<DateTimeOffset>(3), before, (DateTimeOffset)(await apps.Database.NowAsync()).AddSeconds(1));
        Assert.False(await reader.ReadAsync(Cancel));
    }
}
