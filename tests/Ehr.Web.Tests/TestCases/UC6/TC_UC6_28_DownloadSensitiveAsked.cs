using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RecordDownload;
using R4 = Hl7.Fhir.Model;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-28
// Purpose / scenario: Sensitive items are left out of the copy until the patient asks for them on the page, and asking is
//   recorded: as a download of sensitive items for the file, as a view of them for the printable page. The file without
//   them gives no sign that there are any; the page says on screen how many it left out. For a proxy the patient didn't
//   allow sensitive items, they don't exist: the file and the page are the same before and after the record has some,
//   asking or not.
// Type: Functional (alternative flow: sensitive items)
// Requirements: proposed FR-39, FR-19, proposed FR-56
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: As TC-UC6-24 (sertraline marked sensitive; generalized anxiety disorder, sensitive by its code). A second
//   patient, with a medication that isn't sensitive and nothing sensitive yet, has given access without sensitive items
//   to someone signed in and looking at it; later a sensitive medication and diagnosis are added to that record.
// Inputs and system conditions: GET /portal/record?handler=File and /portal/record/print, each with and without
//   &sensitive=true (?sensitive=true), and /portal/record?sensitive=true.
// Test data: FhirRequests.ChartAsync's record; "Synthetic plain medication"; "Synthetic sensitive medication", marked;
//   "Generalized anxiety disorder" (F41.1).
// Steps:
//   1. As the first patient, download without sensitive items, then with them; open the printable page without, then
//      with them; open the page with ?sensitive=true.
//   2. As the second patient's proxy, download and open the printable page, with and without asking; the clinic adds the
//      sensitive items; again.
// Expected result: Step 1: without: neither item, no meta.security and the word "sensitive" nowhere in the file, recorded
//   as a download; the page says "Sensitive items hidden. 2 diagnoses, medications or visit summaries are not shown." with
//   "Show sensitive items". With: both, each marked R (restricted), recorded as a download of sensitive items; the page
//   says "Sensitive items shown. Your view is logged." and tags both "Sensitive", recorded as a view of
//   sensitive items. The page asked with ?sensitive=true has the box ticked. Step 2: every file and every page, but for
//   what each copy has of its own, is the same as the first.
// Creator: Tahseen Arnan
public class TC_UC6_28_DownloadSensitiveAsked(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sensitive_items_come_only_when_asked_and_a_proxy_without_them_sees_no_sign_of_them()
    {
        var (_, patient) = await PortalChartAsync(apps, "Dr. Asked");

        var without = await DownloadAsync(patient.Client);
        Assert.DoesNotContain("sertraline", without.Body);
        Assert.DoesNotContain("anxiety", without.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sensitive", without.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"security\"", without.Body);
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Download, patient.Account, patient.Patient));

        var with = Bundle(await DownloadAsync(patient.Client, sensitive: true));
        var restricted = Entries<R4.MedicationStatement>(with).Where(m => m.Meta?.Security.Any(s => s.Code == "R") == true).Cast<R4.Resource>()
            .Concat(Entries<R4.Condition>(with).Where(c => c.Meta?.Security.Any(s => s.Code == "R") == true)).ToList();
        Assert.Equal(2, restricted.Count);
        Assert.Contains("Generalized anxiety disorder", Entries<R4.Condition>(with).Select(c => c.Code?.Text));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.DownloadSensitive, patient.Account, patient.Patient));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Download, patient.Account, patient.Patient));

        var hidden = await PrintAsync(patient.Client);
        Assert.Contains("Sensitive items hidden. 2 diagnoses, medications or visit summaries are not shown. Show sensitive items", Words(hidden));
        Assert.DoesNotContain(">Sensitive</span>", hidden);
        var views = await apps.AuditEventsAsync(AuditActions.ReadSensitive, patient.Account, patient.Patient);
        var shown = await PrintAsync(patient.Client, sensitive: true);
        Assert.Contains("Sensitive items shown. Your view is logged.", Words(shown));
        Assert.Equal(2, shown.Split(">Sensitive</span>").Length - 1);
        Assert.Contains("sertraline", shown);
        Assert.Contains("Generalized anxiety disorder (F41.1)", Words(shown));
        Assert.Equal(views + 1, await apps.AuditEventsAsync(AuditActions.ReadSensitive, patient.Account, patient.Patient));
        Assert.Contains("name=\"sensitive\" value=\"true\" checked=\"checked\"", await patient.Client.GetStringAsync(PagePath + "?sensitive=true", Cancel));

        // A proxy without sensitive items: the same copies before and after the record has some, asking or not.
        var other = await ProxyTests.PersonAsync(apps, "Synthetic", "Nohint");
        await apps.InsertMedicationAsync(other.Patient, "Synthetic plain medication");
        var (proxy, _) = await VisitSummaryTests.ProxyForAsync(apps, other, "NohintProxy", sensitive: false);
        async Task<string[]> CopiesAsync() =>
        [
            Comparable((await DownloadAsync(proxy.Client)).Body), Comparable((await DownloadAsync(proxy.Client, sensitive: true)).Body),
            Comparable(await PrintAsync(proxy.Client)), Comparable(await PrintAsync(proxy.Client, sensitive: true)),
            Comparable(await proxy.Client.GetStringAsync(PagePath, Cancel)), Comparable(await proxy.Client.GetStringAsync(PagePath + "?sensitive=true", Cancel)),
        ];

        var before = await CopiesAsync();
        Assert.Equal(before[0], before[1]);
        Assert.Equal(before[2], before[3]);
        Assert.Equal(before[4], before[5]);
        Assert.Contains("Synthetic plain medication", before[0]);
        await FhirRequests.InsertAsync(apps,
            "INSERT INTO ehr.medication_statements (id, patient_id, display, status, sensitive) VALUES (@id, @patient, 'Synthetic sensitive medication', 'active', true)", other.Patient);
        await apps.InsertConditionAsync(other.Patient, "Generalized anxiety disorder", code: "F41.1");
        Assert.Equal(before, await CopiesAsync());
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.DownloadSensitive, proxy.Account, other.Patient));
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.ReadSensitive, proxy.Account, other.Patient));
    }
}
