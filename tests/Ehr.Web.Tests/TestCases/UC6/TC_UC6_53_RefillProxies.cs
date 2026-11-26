using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RefillRequestTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-53
// Purpose / scenario: Someone the patient shares their record with asks to renew a medication only when their access
//   includes messages, since asking is writing to the care team; under the record alone they see no link, no page and
//   no requests, and a post is refused. Sensitive medications and their requests follow the rules for sensitive items:
//   the patient sees them once they ask, a proxy without sensitive items never, and a request is gone from such a proxy's
//   page as soon as its medication is marked sensitive. The patient sees a proxy's request as asked by someone they
//   share their record with; a proxy sees only their own as theirs.
// Type: Functional (alternative flow: proxies and sensitive items)
// Requirements: FR-19, proposed FR-58
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has given access with messages (no sensitive items) to one person and to the record alone to
//   another, both signed in and looking at the patient's record. Dr. Test has the patient on their panel.
// Inputs and system conditions: GET and POST /portal/refills, GET /portal, as each of the three, on the on-site app.
// Test data: lisinopril 10 MG Oral Tablet; sertraline 50 MG Oral Tablet, marked sensitive; amlodipine 5 MG Oral Tablet.
// Steps:
//   1. The proxy under the record alone opens the home, the page, and posts a request.
//   2. The proxy with messages opens the page and asks for lisinopril.
//   3. The patient opens the page, asks to see sensitive items, and asks for sertraline.
//   4. The proxy with messages asks for amlodipine; amlodipine is then marked sensitive on the chart.
// Expected result: Step 1: no link; the page and the post send them to the record's home; nothing is saved. Step 2: no
//   sertraline and no word of anything hidden; the request is saved as theirs, and listed "by you". Step 3: one
//   sensitive item hidden until asked for; then sertraline, tagged Sensitive, is asked for; the proxy's lisinopril
//   request reads "by someone you share your record with". Step 4: the proxy's page no longer lists amlodipine's request.
// Creator: Tahseen Arnan
public class TC_UC6_53_RefillProxies(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_proxy_asks_under_access_with_messages_and_sensitive_items_follow_their_rules()
    {
        var (_, _, patient) = await PortalPatientAsync(apps, "Shared");
        var (writer, _) = await ProxyForAsync(apps, patient, "SharedWriter", sensitive: false, scope: "record-and-messages");
        var (reader, _) = await ProxyForAsync(apps, patient, "SharedReader", sensitive: false);
        await MedicationAsync(apps, patient.Patient);
        await MedicationAsync(apps, patient.Patient, "sertraline 50 MG Oral Tablet", "312941", sensitive: true);
        var amlodipine = await MedicationAsync(apps, patient.Patient, "amlodipine 5 MG Oral Tablet", "197361");

        Assert.DoesNotContain("/portal/refills", await reader.Client.GetStringAsync("/portal", Cancel));
        using (var page = await reader.Client.GetAsync(PortalPath, Cancel))
        {
            Assert.Equal("/portal", PathOf(page));
        }

        var token = Token(await reader.Client.GetStringAsync("/portal", Cancel));
        using (var posted = await reader.Client.PostAsync(PortalPath, Form(("Medication", "anything"), ("Note", ""), ("__RequestVerificationToken", token)), Cancel))
        {
            Assert.Equal("/portal", PathOf(posted));
        }

        Assert.Empty(await RequestsAsync(apps, patient.Patient));

        var offered = Words(await writer.Client.GetStringAsync(PortalPath, Cancel));
        Assert.DoesNotContain("sertraline", offered);
        Assert.DoesNotContain("Sensitive items", offered);
        using (var sent = await AskAsync(writer.Client, Lisinopril))
        {
            Assert.Contains("by you", Words(await ReadAsync(sent)));
        }

        Assert.Equal([$"requested|{Lisinopril}|-|{writer.Account}|-|-|-"], await RequestsAsync(apps, patient.Patient));

        var hidden = Words(await patient.Client.GetStringAsync(PortalPath, Cancel));
        Assert.Contains("Sensitive items hidden: 1 item is not shown.", hidden);
        Assert.DoesNotContain("sertraline", hidden);
        Assert.Contains("by someone you share your record with", hidden);
        using (var sent = await AskAsync(patient.Client, "sertraline 50 MG Oral Tablet", sensitive: true))
        {
            var shown = Words(await ReadAsync(sent));
            Assert.Contains("Sensitive items shown: your view of them is recorded.", shown);
            Assert.Contains("sertraline 50 MG Oral Tablet", shown);
        }

        using (var sent = await AskAsync(writer.Client, "amlodipine 5 MG Oral Tablet"))
        {
            Assert.Contains("amlodipine", Words(await ReadAsync(sent)));
        }

        await ProxyTests.QueryAsync(apps,
            $"WITH marked AS (UPDATE ehr.medication_statements SET sensitive = true WHERE id = '{amlodipine}' RETURNING id::text) SELECT id FROM marked", patient.Patient);
        var after = Words(await writer.Client.GetStringAsync(PortalPath, Cancel));
        Assert.DoesNotContain("amlodipine", after);
        Assert.DoesNotContain("sertraline", after);
        Assert.Contains(Lisinopril, after);
    }
}
