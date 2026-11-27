using Ehr.Data.Patients;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.ChartListTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-08
// Purpose / scenario: A clinician changes entries instead of deleting them: a medication is marked stopped and leaves
//   the chart's current list, an allergy recorded before there were codes keeps its name while its details change,
//   and the history shows each change with the old and new values.
// Type: Functional (alternative flow)
// Requirements: FR-02, proposed FR-38
// Use case: UC 4, View a patient's chart (editing the chart's lists)
// Preconditions: The clinician is signed in on the on-site app and the patient is on their panel. The patient has an
//   active medication with a code, and an allergy "Sulfonamides" recorded with no code, as the earlier data was.
// Inputs and system conditions: GET and POST /patients/{id}/medications/{entry} and /patients/{id}/allergies/{entry};
//   GET /patients/{id}/chart-history; on the on-site app.
// Test data: Patient Synthetic Listed. Medication "metformin hydrochloride 500 MG Oral Tablet", stopped 2026-10-01.
//   Allergy "Sulfonamides", reaction changed to "Rash", risk to high and severity to moderate.
// Steps:
//   1. Add the medication; follow its "Change" link.
//   2. Set its status to Stopped with the stop date, and save.
//   3. Follow the allergy's "Change" link, enter the reaction, risk and severity, leave the name as it is, and save.
//   4. Open the chart and the history.
// Expected result: The change form is titled "Change medication" and opens filled in. After step 2 the chart says "No
//   active medications recorded." and the row still exists, stopped. After step 3 the allergy is still "Sulfonamides"
//   with no code, and shows "Rash". No row was deleted. The history shows, in the form's words, "Status: Active →
//   Stopped", "Stopped: not recorded → 2026-10-01", "Reaction: not recorded → Rash", "Risk: not recorded → High
//   risk" and "Severity: not recorded → Moderate".
// Creator: Tahseen Arnan (expansion step 2), UC 4 owner Diego Valenzuela
public class TC_UC4_08_ChangeEntryAlternativeFlow(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_entry_is_changed_or_marked_stopped_and_never_deleted()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        await apps.InsertAllergyAsync(patient, "Sulfonamides");
        using var added = await SaveAsync(session.Client, patient, "medications", null, ("Name", "metformin hydrochloride 500 MG Oral Tablet"), ("Status", "active"));
        var medication = await IdAsync(apps, "medication_statements", patient);
        var allergy = await IdAsync(apps, "allergy_intolerances", patient);

        var chart = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.Contains($"<a href=\"{EntryPath(patient, "medications", medication)}\" aria-label=\"Change: metformin hydrochloride 500 MG Oral Tablet\" data-change-entry=\"medications\">Change</a>", chart);
        var form = await session.Client.GetStringAsync(EntryPath(patient, "medications", medication), Cancel);
        Assert.Contains("Change medication: Listed, Synthetic", form);
        Assert.Contains("name=\"Name\" value=\"metformin hydrochloride 500 MG Oral Tablet\"", form);
        Assert.Contains("<option value=\"active\" selected=\"selected\">Active</option>", form);

        using var stopped = await SaveAsync(session.Client, patient, "medications", medication,
            ("Name", "metformin hydrochloride 500 MG Oral Tablet"), ("Status", MedicationStatementStatus.Stopped), ("Stopped", "2026-10-01"));
        using var reaction = await SaveAsync(session.Client, patient, "allergies", allergy, ("Name", "sulfonamides"), ("Reaction", "Rash"), ("Status", "active"),
            ("Criticality", "high"), ("Severity", "moderate"));

        var after = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.Contains("No active medications.", after);
        Assert.Contains("<strong>Sulfonamides</strong>: Rash", after);
        Assert.Equal(["metformin hydrochloride 500 MG Oral Tablet stopped 2026-10-01"],
            await RowsAsync(apps, "medication_statements", patient, "concat_ws(' ', display, status, effective_end)"));
        Assert.Equal(["Sulfonamides - Rash"], await RowsAsync(apps, "allergy_intolerances", patient, "concat_ws(' ', display, coalesce(code, '-'), reaction)"));

        var history = Words(await session.Client.GetStringAsync($"/patients/{patient}/chart-history", Cancel));
        Assert.Contains("Status: Active → Stopped", history);
        Assert.Contains("Stopped: not recorded → 2026-10-01", history);
        Assert.Contains("Reaction: not recorded → Rash", history);
        Assert.Contains("Risk: not recorded → High risk", history);
        Assert.Contains("Severity: not recorded → Moderate", history);
        Assert.DoesNotContain("→ stopped", history);
    }
}
