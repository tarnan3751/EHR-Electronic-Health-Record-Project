using System.Net;
using System.Text.RegularExpressions;
using Ehr.Data.Audit;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.ChartListTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-07
// Purpose / scenario: A clinician adds a diagnosis, a medication and an allergy to a patient's chart, each picked from
//   the standard codes. The chart lists them, the history records who added each, and the audit log records the writes.
// Type: Functional (successful flow)
// Requirements: FR-02, proposed FR-38
// Use case: UC 4, View a patient's chart (editing the chart's lists)
// Preconditions: The clinician is signed in on the on-site app and the patient is on their panel, with nothing in the
//   three lists. The reference codes are loaded. The clinic's server is up.
// Inputs and system conditions: GET /patients/{id}; GET and POST /patients/{id}/diagnoses, /medications and
//   /allergies; GET /patients/{id}/chart-history; on the on-site app.
// Test data: Patient Synthetic Listed. Diagnosis "Essential (primary) hypertension" (ICD-10-CM I10), onset 2024-05-01.
//   Medication "lisinopril 10 MG Oral Tablet" (RxNorm 314076), "1 tablet by mouth daily". Allergy "Penicillin G"
//   (RxNorm 7980), reaction "Hives", risk high. Clinician "Dr. Test".
// Steps:
//   1. Open the chart and follow "Add diagnosis".
//   2. Enter the diagnosis and its onset and choose "Save diagnosis".
//   3. Add the medication and the allergy the same way.
//   4. Open the chart, then "History of allergies, medications and diagnoses".
// Expected result: The chart offers "Add allergy", "Add medication" and "Add diagnosis". The form opens with the
//   cursor in a required name field that searches the codes, shows the patient's banner, and is one recorded read.
//   After saving, the chart lists "Essential (primary) hypertension (I10) since May 1, 2024", the medication with how
//   it is taken, and the allergy with "High risk", each with a "Change" link; the banner, apart from the list, shows
//   the allergy as "High risk: Penicillin G: Hives". Each row holds its code. The history's table has three rows, one
//   per entry, each "Added" with "Dr. Test" as its author. The write audit has, by the clinician, one insert into
//   each list and one into the history for each entry.
// Creator: Tahseen Arnan (expansion step 2), UC 4 owner Diego Valenzuela
public class TC_UC4_07_AddEntrySuccessfulFlow(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_clinician_adds_coded_entries_and_the_chart_history_and_audit_show_them()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);

        var chart = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);
        foreach (var (list, label) in new[] { ("allergies", "Add allergy"), ("medications", "Add medication"), ("diagnoses", "Add diagnosis") })
        {
            Assert.Contains($"<a href=\"{EntryPath(patient, list)}\" class=\"sc-btn sc-btn--compact\" data-add-entry=\"{list}\">{label}</a>", chart);
        }

        var reads = await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient);
        var form = await session.Client.GetStringAsync(EntryPath(patient, "diagnoses"), Cancel);
        Assert.Contains("<h1>Add diagnosis</h1>", form);
        Assert.Matches("<input [^>]*name=\"Name\"[^>]*required[^>]*autofocus[^>]*data-code-search=\"condition\"", form);
        Assert.Contains("/_content/Ehr.Design/js/code-search.", form);
        Assert.Contains(">Save diagnosis</button>", form);
        Assert.DoesNotContain("htmx.min.js", form);
        Assert.Equal(reads + 1, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient));

        using var diagnosis = await SaveAsync(session.Client, patient, "diagnoses", null,
            ("Name", "Essential (primary) hypertension"), ("Onset", "2024-05-01"), ("Status", "active"));
        Assert.Equal(HttpStatusCode.OK, diagnosis.StatusCode);
        Assert.Equal($"/patients/{patient}", diagnosis.RequestMessage!.RequestUri!.AbsolutePath);
        using var medication = await SaveAsync(session.Client, patient, "medications", null,
            ("Name", "lisinopril 10 MG Oral Tablet"), ("Dosage", "1 tablet by mouth daily"), ("Status", "active"));
        using var allergy = await SaveAsync(session.Client, patient, "allergies", null,
            ("Name", "Penicillin G"), ("Reaction", "Hives"), ("Criticality", "high"), ("Status", "active"));

        var saved = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.Matches(@"<strong>Essential \(primary\) hypertension</strong>\s*\(I10\)\s*since May 1, 2024", saved);
        Assert.Contains("<strong>lisinopril 10 MG Oral Tablet</strong>: 1 tablet by mouth daily", saved);
        // The allergy in the chart's list, with its tag, and in the banner, apart.
        var allergies = Regex.Match(saved, "<section class=\"sc-panel\" aria-labelledby=\"chart-allergies\">[\\s\\S]*?</section>").Value;
        Assert.Matches("<strong>Penicillin G</strong>: Hives\\s*<span class=\"sc-tag sc-tag--danger\">High risk</span>", allergies);
        var banner = Regex.Match(saved, "<section class=\"sc-banner\"[\\s\\S]*?</section>").Value;
        Assert.Contains("<li class=\"sc-allergy sc-allergy--high\">High risk: <strong>Penicillin G</strong>: Hives</li>", banner);
        Assert.Equal(3, Regex.Matches(saved, "data-change-entry=").Count);
        Assert.Equal(["Essential (primary) hypertension I10 active 2024-05-01"],
            await RowsAsync(apps, "conditions", patient, "concat_ws(' ', display, code, clinical_status, onset_date)"));
        Assert.Equal(["lisinopril 10 MG Oral Tablet 314076"], await RowsAsync(apps, "medication_statements", patient, "concat_ws(' ', display, code)"));
        Assert.Equal(["Penicillin G 7980 rxnorm medication"], await RowsAsync(apps, "allergy_intolerances", patient, "concat_ws(' ', display, code, code_system, category)"));

        // The history's rows: each "By" cell names the clinician, and each change is "Added".
        var history = await session.Client.GetStringAsync($"/patients/{patient}/chart-history", Cancel);
        var rows = Regex.Matches(Regex.Match(history, "<tbody>[\\s\\S]*?</tbody>").Value, "<tr>([\\s\\S]*?)</tr>")
            .Select(row => Regex.Matches(row.Groups[1].Value, "<td[^>]*>([\\s\\S]*?)</td>").Select(cell => Words(cell.Groups[1].Value).Trim()).ToList())
            .ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, cells => Assert.Equal(("Dr. Test", "Added"), (cells[1], cells[4])));
        Assert.Equal(["Allergy Penicillin G", "Diagnosis Essential (primary) hypertension", "Medication lisinopril 10 MG Oral Tablet"],
            rows.Select(cells => $"{cells[2]} {cells[3]}").Order());

        // The write audit: one insert by the clinician into each list, and one into the history for each entry.
        Assert.Equal(["allergy_intolerances 1", "chart_history 3", "conditions 1", "medication_statements 1"], await InsertsAsync(clinician.Id, patient));
    }

    async Task<List<string>> InsertsAsync(Guid clinician, Guid patient)
    {
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT table_name || ' ' || count(*) FROM ehr.audit_events
            WHERE action = @action AND actor_id = @clinician AND patient_id = @patient AND table_name <> 'patients'
            GROUP BY table_name ORDER BY table_name
            """, db);
        command.Parameters.AddWithValue("action", AuditActions.Insert);
        command.Parameters.AddWithValue("clinician", clinician);
        command.Parameters.AddWithValue("patient", patient);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        while (await reader.ReadAsync(Cancel))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
