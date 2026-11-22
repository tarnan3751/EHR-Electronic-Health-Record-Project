using System.Net;
using System.Text.Json;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.LabTests;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.ResultTests;

namespace Ehr.Web.Tests.TestCases.UC20;

// Test Case ID: TC-UC20-06
// Purpose / scenario: The lab is told only what it needs to produce results, and nothing about a patient leaks from
//   the door: the hand-over holds exactly the contract's fields, never the patient's name, MRN, birth date or ID, and no
//   sensitive diagnosis; no answer is kept in a cache; no refusal and no log line carries a patient's details, an order's
//   ID or the key. Each order handed over and each result filed is in the audit log as the lab's, with no account, and
//   the administrators' audit page says "Lab".
// Type: Non-functional (security: what is disclosed, kept and recorded)
// Requirements: NFR-06, with proposed FR-54
// Use case: UC 20, Receive results from a lab
// Preconditions: A lab is connected. A clinician on the on-site app has the patient on their panel and has signed a
//   complete blood count for them through the order form. The patient has type 2 diabetes (E11.9) and depression
//   (F32.A, sensitive by its code) on their list. The on-site app's log lines are kept. An admin is signed in.
// Inputs and system conditions: GET /api/lab/orders and POST /api/lab/orders/{id}/results with the lab's key, and both
//   with a wrong key, on the on-site app; GET /admin/audit as the admin.
// Test data: Hemoglobin (718-7) 9.1 g/dL, and the same in g/L; the wrong key "not-the-lab-key-but-long-enough-to-look-real".
// Steps:
//   1. As the lab, ask for the orders signed since the test began, then post hemoglobin 9.1 for the order, then post
//      it in g/L to the same order.
//   2. Ask and post with the wrong key.
//   3. Read the log lines, the audit events about the patient, and the audit page for the patient.
// Expected result: Step 1: 200 with exactly watermark, more and orders; the order with exactly id, signedAt, tests and
//   conditions, signedAt in UTC with a Z; each test with exactly code, unit, low, high and decimals; conditions
//   ["E11.9"]; none of the patient's ID, MRN, family name, birth date or F32.A anywhere in the answer. Then 204, then
//   422 wrong-unit. Step 2: 401 unauthorized each. Every answer is no-store. Step 3: a warning that a key was wrong,
//   without either key; no log line holds the patient's ID, MRN, family name or birth date, or the order's ID; the
//   audit log holds one sent_to_lab for the order and the result's, the order's and the inbox item's writes, all with
//   no account and the lab as the source; the audit page shows sent_to_lab and "Lab" as who.
// Creator: Tahseen Arnan
public class TC_UC20_06_Nfr06_Security(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    const string WrongKey = "not-the-lab-key-but-long-enough-to-look-real";

    [Fact]
    public async Task The_lab_is_told_only_what_it_needs_and_nothing_about_a_patient_leaks_from_the_door()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        await apps.InsertConditionAsync(patient, "Type 2 diabetes mellitus without complications", code: "E11.9");
        await apps.InsertConditionAsync(patient, "Depression, unspecified", code: "F32.A");
        var since = await WatermarkAsync(apps);
        var order = await SignedAsync(session.Client, patient, "lab", "Complete blood count (CBC)", Cbc);
        await SettledAsync(apps, patient);
        var logs = new LoggingTests.CapturedLogs();
        await using var app = apps.OnPrem.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<ILoggerProvider>(logs)));
        var lab = Client(app);
        var answers = new List<HttpResponseMessage>();

        // Step 1.
        var (handed, body) = await HandOverAsync(lab, since);
        answers.Add(handed);
        Assert.Equal(HttpStatusCode.OK, handed.StatusCode);
        Assert.Equal("application/json", handed.Content.Headers.ContentType?.MediaType);
        Assert.Equal(["more", "orders", "watermark"], Names(body));
        var only = Assert.Single(body.GetProperty("orders").EnumerateArray());
        Assert.Equal(order, only.GetProperty("id").GetGuid());
        Assert.Equal(["conditions", "id", "signedAt", "tests"], Names(only));
        Assert.All(only.GetProperty("tests").EnumerateArray(), test => Assert.Equal(["code", "decimals", "high", "low", "unit"], Names(test)));
        Assert.Equal(["E11.9"], only.GetProperty("conditions").EnumerateArray().Select(c => c.GetString()));
        Assert.EndsWith("Z", only.GetProperty("signedAt").GetString(), StringComparison.Ordinal);
        var text = body.GetRawText();
        foreach (var personal in new[] { patient.ToString(), WebApps.MrnOf(patient), "Orderable", "1980-01-01", "F32.A", "Depression" })
        {
            Assert.DoesNotContain(personal, text, StringComparison.OrdinalIgnoreCase);
        }

        DateTimeOffset collected = (await apps.Database.NowAsync()).AddMinutes(-15);
        var filed = await PostAsync(lab, order, collected, (Hemoglobin, 9.1m, "g/dL"));
        answers.Add(filed);
        Assert.Equal(HttpStatusCode.NoContent, filed.StatusCode);
        var wrongUnit = await PostAsync(lab, order, collected, (Platelets, 250m, "per uL"));
        answers.Add(wrongUnit);
        Assert.Equal("wrong-unit", await RefusalAsync(wrongUnit));

        // Step 2.
        var intruder = Client(app, WrongKey);
        var (asked, _) = await HandOverAsync(intruder, since);
        var posted = await PostAsync(intruder, order, collected, (Platelets, 250m, "10*3/uL"));
        answers.AddRange([asked, posted]);
        Assert.Equal(HttpStatusCode.Unauthorized, asked.StatusCode);
        Assert.Equal("unauthorized", await RefusalAsync(posted));
        Assert.All(answers, answer => Assert.Equal("no-store", answer.Headers.CacheControl?.ToString()));

        // Step 3.
        var lines = logs.Messages.ToList();
        Assert.Contains(lines, line => line.StartsWith("Warning Ehr.Web.Api.Lab.LabApi: ", StringComparison.Ordinal) && line.Contains("its key is wrong", StringComparison.Ordinal));
        foreach (var secret in new[] { patient.ToString(), WebApps.MrnOf(patient), "Orderable", "1980-01-01", order.ToString(), MigratedDatabase.LabKey, WrongKey })
        {
            Assert.DoesNotContain(lines, line => line.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }

        Assert.Equal(
            ["sent_to_lab orders - lab", "insert observations - lab", "update orders - lab", "insert inbox_items - lab"],
            await LabEventsAsync(apps, patient));
        Assert.Equal([$"{order}"], await ProxyTests.QueryAsync(apps,
            "SELECT row_id::text FROM ehr.audit_events WHERE patient_id = @patient AND action = 'sent_to_lab' AND actor_id IS NULL AND details IS NULL", patient));

        var admin = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Synthetic Admin", StaffRoles.Admin));
        var page = await admin.Client.GetStringAsync($"/admin/audit?Patient={patient}&Action={AuditActions.SentToLab}", Cancel);
        Assert.Matches($"(?s)<td>Lab</td>\\s*<td>sent_to_lab</td>\\s*<td class=\"sc-table__id\">{patient}</td>\\s*<td class=\"sc-table__id\">orders {order}</td>", page);
        Assert.Contains("<td>Lab</td>", await admin.Client.GetStringAsync($"/admin/audit?Patient={patient}&Action={AuditActions.Insert}", Cancel));
    }

    static List<string> Names(JsonElement element) => [.. element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];
}
