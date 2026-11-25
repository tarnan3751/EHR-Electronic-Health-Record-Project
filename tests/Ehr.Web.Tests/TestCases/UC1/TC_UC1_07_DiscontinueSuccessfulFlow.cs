using System.Net;
using Ehr.Data.Audit;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC1;

// Test Case ID: TC-UC1-07
// Purpose / scenario: A clinician discontinues an active order from the chart, with a reason. The order leaves the
//   active orders and is listed under the chart's discontinued orders with who, when and why. The database sets who
//   and when, and the audit log records the change.
// Type: Functional (successful flow)
// Requirements: FR-05, proposed FR-37
// Use case: UC 1, Place an order (changing a signed order)
// Preconditions: The clinician is signed in on the on-site app and the patient is on their panel. The patient has one
//   active order, signed in step 1. The clinic's server is up.
// Inputs and system conditions: GET /patients/{id}, GET and POST /patients/{id}/orders/{order}/discontinue as
//   js/orders.js sends it, GET /patients/{id}/orders/discontinued, on the on-site app.
// Test data: Patient Synthetic Orderable, allergic to Penicillin (hives, high risk). Order "Complete blood count
//   (CBC)" (SYN-LAB-001), Reason for the test "Synthetic fatigue". Reason for discontinuing "Synthetic: symptoms
//   resolved". Clinician "Dr. Test".
// Steps:
//   1. Sign a Complete blood count (CBC) order and open the chart.
//   2. Follow the order's "Discontinue…" link.
//   3. Enter the reason and choose "Discontinue order".
//   4. Open the address the answer gives (the chart), then "Discontinued orders".
//   5. Read the order and its audit row from the database.
// Expected result: The chart offers "Change" and "Discontinue…" for the order and a "Discontinued orders" link. The
//   discontinue page shows the patient's banner with allergies and the order, with the cursor in a required reason
//   field, and is one recorded read. After step 3 the chart says "Order discontinued:", naming the order, and "No
//   active orders."; the discontinued orders list shows the order, "Dr. Test", and the reason. The order's row is revoked (FHIR's code for
//   discontinued) with the reason, the clinician as discontinuer and the time of the change; no other column changed;
//   one audit row for the update names the clinician.
// Creator: Tahseen Arnan
public class TC_UC1_07_DiscontinueSuccessfulFlow(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_clinician_discontinues_an_order_with_a_reason_and_the_chart_keeps_it_as_discontinued()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        var order = await SignedAsync(session.Client, patient, "lab", "Complete blood count (CBC)", Cbc);

        var chart = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.Matches($"<a [^>]*data-change-order[^>]*href=\"/patients/{patient}/orders/new\\?type=lab&amp;replaces={order}\"[^>]*>Change</a>", chart);
        Assert.Matches($"<a [^>]*data-discontinue-order[^>]*href=\"{DiscontinuePath(patient, order)}\"[^>]*>Discontinue…</a>", chart);
        Assert.Contains($"href=\"/patients/{patient}/orders/discontinued\">Discontinued orders</a>", chart);

        var reads = await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient);
        var form = await session.Client.GetStringAsync(DiscontinuePath(patient, order), Cancel);
        Assert.Contains("Discontinue order: Orderable, Synthetic", form);
        Assert.Contains("<strong>Penicillin</strong>: Hives", form);
        Assert.Contains("<strong>Complete blood count (CBC)</strong> Reason for the test: Synthetic fatigue", form);
        Assert.Matches("<textarea [^>]*name=\"Reason\"[^>]*required autofocus", form);
        Assert.Contains(">Discontinue order</button>", form);
        Assert.DoesNotContain("htmx.min.js", form);
        Assert.Equal(reads + 1, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient));

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        using var discontinued = await DiscontinueAsync(session.Client, patient, order, "  Synthetic: symptoms resolved ");
        Assert.Equal(HttpStatusCode.OK, discontinued.StatusCode);
        var url = System.Text.Json.JsonDocument.Parse(await discontinued.Content.ReadAsStringAsync(Cancel)).RootElement.GetProperty("url").GetString()!;

        var confirmed = await session.Client.GetStringAsync(url, Cancel);
        Assert.Contains("Order discontinued: Complete blood count (CBC) is listed under this chart's discontinued orders.", Words(confirmed));
        Assert.Contains("No active orders.", confirmed);
        Assert.DoesNotContain("data-discontinue-order", confirmed);

        var listed = await session.Client.GetStringAsync($"/patients/{patient}/orders/discontinued", Cancel);
        Assert.Contains("Discontinued orders: Orderable, Synthetic", listed);
        Assert.Contains("<strong>Complete blood count (CBC)</strong> (Lab)", listed);
        Assert.Contains("Reason for the test: Synthetic fatigue", listed);
        Assert.Contains("<td>Dr. Test</td>", listed);
        Assert.Contains("Synthetic: symptoms resolved", listed);
        Assert.DoesNotContain(">Changed</span>", listed);

        Assert.Equal($"revoked|Synthetic: symptoms resolved|{clinician.Id}|-|-", await RowAsync(apps, order));
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT o.discontinued_at, o.catalog_code, o.details ->> 'indication', o.signer_id, e.actor_id, e.details -> 'status' ->> 'new'
            FROM ehr.orders o JOIN ehr.audit_events e ON e.row_id = o.id AND e.action = 'update' AND e.table_name = 'orders'
            WHERE o.id = @order
            """, db);
        command.Parameters.AddWithValue("order", order);
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        Assert.True(await reader.ReadAsync(Cancel));
        Assert.InRange(reader.GetFieldValue<DateTimeOffset>(0), before, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal("SYN-LAB-001", reader.GetString(1));
        Assert.Equal("Synthetic fatigue", reader.GetString(2));
        Assert.Equal(clinician.Id, reader.GetGuid(3));
        Assert.Equal(clinician.Id, reader.GetGuid(4));
        Assert.Equal("revoked", reader.GetString(5));
        Assert.False(await reader.ReadAsync(Cancel));
    }
}
