using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.ChartListTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-32
// Purpose / scenario: Sensitive items are left out of the chart until the clinician asks for them, and each view that
//   shows them is recorded. A diagnosis is sensitive by its code, a medication because the clinician marked it; the
//   chart, the patient banner, the history and the desktop app's offline copy never show either by accident, and
//   asking to see them shows them tagged, with the view recorded as read_sensitive.
// Type: Functional (alternative flow)
// Requirements: FR-02, proposed FR-39
// Use case: UC 4, View a patient's chart (editing the chart's lists)
// Preconditions: The clinician is signed in on the on-site app, and in the desktop app, and the patient is on their
//   panel with nothing in the three lists. The reference codes are loaded. The clinic's server is up.
// Inputs and system conditions: POST /patients/{id}/diagnoses and /medications; GET /patients/{id},
//   /patients/{id}?sensitive=true, /patients/{id}/chart-history[?sensitive=true] and /patients/{id}/diagnoses/{entry};
//   POST /api/desktop/offline-panel; on the on-site app.
// Test data: Patient Synthetic Sensitive case. Diagnoses "Generalized anxiety disorder" (ICD-10-CM F41.1, sensitive
//   by its code, saved with the box unchecked) and "Essential (primary) hypertension" (I10). Medication "sertraline 50
//   MG Oral Tablet", marked sensitive by the clinician. Clinician "Dr. Test".
// Steps:
//   1. Add the two diagnoses and the medication.
//   2. Open the chart; count the clinician's recorded views of sensitive items.
//   3. Choose "Show sensitive items".
//   4. Open the history, then choose "Show sensitive items" there.
//   5. Open the anxiety diagnosis's form.
//   6. Download the desktop app's offline copy.
// Expected result: After step 1 the anxiety diagnosis is stored sensitive and the hypertension not. Step 2: the chart
//   lists the hypertension but neither anxiety nor sertraline, anywhere on the page, banner included, and says
//   "Sensitive items hidden: 2 diagnoses or medications are not shown." with a "Show sensitive items" link; nothing
//   is recorded as read_sensitive. Step 3: both are listed, each with a "Sensitive" tag, the chart says "Sensitive
//   items shown: your view of them is recorded.", the banner still names neither, and one read_sensitive is recorded
//   and no plain read. Step 4: the history leaves their changes out ("Sensitive items hidden: 2 changes are not
//   shown.") until asked, then shows them, recorded the same way. Step 5: the form shows the diagnosis with its
//   "Sensitive" box fixed ("Sensitive by its code."), and is recorded as read_sensitive. Step 6: the offline copy
//   holds the hypertension only and says "sensitiveNotKept": 2.
// Creator: Tahseen Arnan (expansion step 2), UC 4 owner Diego Valenzuela
public class TC_UC4_32_SensitiveItems(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sensitive_items_are_left_out_until_asked_for_and_each_view_of_them_is_recorded()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps, "Sensitive case");
        using var anxiety = await SaveAsync(session.Client, patient, "diagnoses", null, ("Name", "Generalized anxiety disorder"), ("Status", "active"));
        using var hypertension = await SaveAsync(session.Client, patient, "diagnoses", null, ("Name", "Essential (primary) hypertension"), ("Status", "active"));
        using var sertraline = await SaveAsync(session.Client, patient, "medications", null, ("Name", "sertraline 50 MG Oral Tablet"), ("Status", "active"), ("Sensitive", "true"));
        Assert.Equal(["Essential (primary) hypertension I10 false", "Generalized anxiety disorder F41.1 true"],
            await RowsAsync(apps, "conditions", patient, "concat_ws(' ', display, code, sensitive::text)"));
        Task<long> SensitiveViewsAsync() => apps.AuditEventsAsync(AuditActions.ReadSensitive, clinician.Id, patient);
        Task<long> ReadsAsync() => apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient);

        var views = await SensitiveViewsAsync();
        var chart = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.Contains("<strong>Essential (primary) hypertension</strong>", chart);
        Assert.DoesNotContain("anxiety", chart, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sertraline", chart, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sensitive items hidden: 2 diagnoses or medications are not shown. Show sensitive items", Words(chart));
        Assert.Equal(views, await SensitiveViewsAsync());

        var reads = await ReadsAsync();
        var shown = await session.Client.GetStringAsync($"/patients/{patient}?sensitive=true", Cancel);
        Assert.Matches("<strong>Generalized anxiety disorder</strong>[^<]*\\(F41\\.1\\)[\\s\\S]*?<span class=\"sc-tag sc-tag--warning\">Sensitive</span>", shown);
        Assert.Matches("<strong>sertraline 50 MG Oral Tablet</strong>\\s*<span class=\"sc-tag sc-tag--warning\">Sensitive</span>", shown);
        Assert.Contains("Sensitive items shown: your view of them is recorded.", Words(shown));
        var banner = System.Text.RegularExpressions.Regex.Match(shown, "<section class=\"sc-banner\"[\\s\\S]*?</section>").Value;
        Assert.NotEmpty(banner);
        Assert.DoesNotContain("anxiety", banner, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sertraline", banner, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(views + 1, await SensitiveViewsAsync());
        Assert.Equal(reads, await ReadsAsync());

        var history = await session.Client.GetStringAsync($"/patients/{patient}/chart-history", Cancel);
        Assert.DoesNotContain("anxiety", history, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sertraline", history, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sensitive items hidden: 2 changes are not shown.", Words(history));
        Assert.Equal(views + 1, await SensitiveViewsAsync());
        var historyShown = await session.Client.GetStringAsync($"/patients/{patient}/chart-history?sensitive=true", Cancel);
        Assert.Contains("Generalized anxiety disorder", historyShown);
        Assert.Contains("sertraline 50 MG Oral Tablet", historyShown);
        Assert.Equal(views + 2, await SensitiveViewsAsync());

        var entry = Guid.Parse((await RowsAsync(apps, "conditions", patient, "id::text", "display DESC")).First());
        var form = await session.Client.GetStringAsync(EntryPath(patient, "diagnoses", entry), Cancel);
        Assert.Contains("value=\"Generalized anxiety disorder\"", form);
        Assert.Contains("Sensitive by its code.", Words(form));
        Assert.Equal(views + 3, await SensitiveViewsAsync());

        using var offline = await DesktopSignInTests.PostAsync(apps.OnPrem, "/api/desktop/offline-panel", await apps.DesktopSignInAsync(apps.OnPrem, clinician));
        var copy = await offline.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("Essential (primary) hypertension", copy);
        Assert.DoesNotContain("anxiety", copy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sertraline", copy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"sensitiveNotKept\":2", copy);
    }
}
