using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.ProxyTests;
using static Ehr.Web.Tests.RecordDownload;
using R4 = Hl7.Fhir.Model;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-27
// Purpose / scenario: Someone the patient shares their record with downloads and prints it as their access allows, under
//   each scope a grant has ("record", "record-and-messages"), with sensitive items allowed and not: sensitive items only
//   when the patient allowed them and the proxy asks; without that, no box to ask with and nothing more for asking. What
//   they do is recorded under their own account and the patient. A proxy whose access was removed gets nothing more of
//   the record, and an account with no record of its own that isn't looking at a shared one is sent to choose one.
// Type: Functional (alternative flow: proxies)
// Requirements: FR-19, proposed FR-44, proposed FR-46, proposed FR-56
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: As TC-UC6-24, and the patient has given access to four people, one under each scope with sensitive items
//   and one under each without; each is signed in and looking at the patient's record. Then the patient removes one's
//   access; and invites a carer with no record, who creates an account and signs in.
// Inputs and system conditions: GET /portal/record, /portal/record?handler=File (with and without &sensitive=true) and
//   /portal/record/print, as each, on the on-site app.
// Test data: FhirRequests.ChartAsync's record, whose sertraline and generalized anxiety disorder are sensitive.
// Steps:
//   1. As each proxy, open Download this record, download the file without and with sensitive items, and open the
//      printable page.
//   2. The patient removes the access of the proxy under "record" without sensitive items, who downloads again.
//   3. The carer with no record of their own, not looking at one, downloads and opens the pages.
// Expected result: Step 1: each sees "Download this record" for "Synthetic Fhirchart"; the two allowed sensitive items are
//   offered the box, and their file with sensitive items holds sertraline and the anxiety disorder, marked restricted,
//   recorded as a download of sensitive items; the other two get no box, and the same file whether they ask or not, with
//   neither. Every event is under the proxy's account and the patient. Step 2: sent to their own record, which says the
//   access ended, with nothing of the patient's; from then on the copy is of their own record. Step 3: sent to "Whose record?" each time; nothing is recorded.
// Creator: Tahseen Arnan
public class TC_UC6_27_DownloadRecordProxies(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_proxy_gets_the_copy_their_grant_allows_and_an_ended_one_gets_nothing()
    {
        var (chart, patient) = await PortalChartAsync(apps, "Dr. Proxies");
        var proxies = new List<(Person Proxy, Guid Grant, string Scope, bool Sensitive)>();
        foreach (var (scope, sensitive, family) in new[]
        {
            ("record", true, "RecordAllowed"), ("record", false, "RecordWithout"),
            ("record-and-messages", true, "MessagesAllowed"), ("record-and-messages", false, "MessagesWithout"),
        })
        {
            var (proxy, grant) = await VisitSummaryTests.ProxyForAsync(apps, patient, family, sensitive, scope);
            proxies.Add((proxy, grant, scope, sensitive));
        }

        foreach (var (proxy, _, _, sensitive) in proxies)
        {
            var page = await proxy.Client.GetStringAsync(PagePath, Cancel);
            Assert.Contains("<h1>Download this record</h1>", page);
            Assert.Contains("A copy of Synthetic Fhirchart's allergies", Words(page));
            Assert.Equal(sensitive, page.Contains("name=\"sensitive\"", StringComparison.Ordinal));

            var plain = await DownloadAsync(proxy.Client);
            var asked = await DownloadAsync(proxy.Client, sensitive: true);
            var withSensitive = Bundle(asked);
            Assert.Equal("Fhirchart", Entries<R4.Patient>(withSensitive).Single().Name.Single().Family);
            Assert.DoesNotContain("sertraline", plain.Body);
            if (sensitive)
            {
                var sertraline = Entries<R4.MedicationStatement>(withSensitive).Single(m => (m.Medication as R4.CodeableConcept)?.Text?.StartsWith("sertraline", StringComparison.Ordinal) == true);
                Assert.Equal("R", sertraline.Meta?.Security.Single().Code);
                Assert.Contains("Generalized anxiety disorder", Entries<R4.Condition>(withSensitive).Select(c => c.Code?.Text));
                Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.DownloadSensitive, proxy.Account, patient.Patient));
            }
            else
            {
                Assert.Equal(Comparable(plain.Body), Comparable(asked.Body));
                Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.DownloadSensitive, proxy.Account, patient.Patient));
                Assert.DoesNotContain("Sensitive items", Words(await PrintAsync(proxy.Client, sensitive: true)));
            }

            Assert.Equal(sensitive ? 1 : 2, await apps.AuditEventsAsync(AuditActions.Download, proxy.Account, patient.Patient));
            Assert.Contains("<h1>Health record of Synthetic Fhirchart</h1>", await PrintAsync(proxy.Client));
            // Nothing recorded against the proxy's own record: under either scope, the copy is the patient's.
            Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.Download, proxy.Account, proxy.Patient));
        }

        // Access removed: the next request goes back to the proxy's own record, which says so.
        var ended = proxies[1];
        using (var removed = await PostFormAsync(patient.Client, "/portal/access?handler=Revoke", "/portal/access", ("grantId", ended.Grant)))
        {
            Assert.Contains("Removed:", await ReadAsync(removed));
        }

        var downloads = await apps.AuditEventsAsync(AuditActions.Download, ended.Proxy.Account, patient.Patient);
        using (var after = await ended.Proxy.Client.GetAsync(FilePath, Cancel))
        {
            Assert.Equal("/portal", PathOf(after));
            var own = await ReadAsync(after);
            Assert.Contains("Access ended: you can no longer view that record. This is your own.", Words(own));
            Assert.DoesNotContain("Fhirchart", own);
        }

        // From then on the copy is of their own record.
        Assert.Contains("<h1>Health record of Synthetic RecordWithout</h1>", await PrintAsync(ended.Proxy.Client));
        Assert.Equal("Recordwithout", Entries<R4.Patient>(Bundle(await DownloadAsync(ended.Proxy.Client))).Single().Name.Single().Family, ignoreCase: true);

        Assert.Equal(downloads, await apps.AuditEventsAsync(AuditActions.Download, ended.Proxy.Account, patient.Patient));

        // An account with no record of its own, not looking at a shared one: sent to choose, nothing recorded.
        var carer = $"synthetic-carer-{Guid.NewGuid():N}@example.com";
        Assert.Contains("Saved:", await GiveAsync(patient, carer));
        Assert.Contains("Account created.", await AcceptAsync(apps, InvitationPath(apps, carer), "Synthetic Record Carer"));
        var session = await SignInAsync(apps, carer);
        Assert.Equal("/portal/choose", session.Path);
        foreach (var path in new[] { FilePath, PrintPath, PagePath })
        {
            using var sent = await session.Client.GetAsync(path, Cancel);
            Assert.Equal("/portal/choose", PathOf(sent));
            var choose = await ReadAsync(sent);
            Assert.Contains("<h1>Whose record?</h1>", choose);
            Assert.DoesNotContain(chart.Mrn, choose);
        }

        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.Download, await AccountIdAsync(apps, carer)));
    }
}
