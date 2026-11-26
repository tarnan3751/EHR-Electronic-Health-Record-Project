using Ehr.Data.Accounts;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.ChartListTests;

namespace Ehr.Web.Tests.TestCases.UC12;

// Test Case ID: TC-UC12-07
// Purpose / scenario: An admin reviewing the audit log sees that a sensitive item changed, and who changed it, but
//   never its values. Marking an item sensitive later hides the values of its earlier events as well. (Since step 18a
//   the values of an ordinary change aren't shown either: TC-UC12-09 checks what is shown of one instead.)
// Type: Functional (alternative flow)
// Requirements: FR-31, proposed FR-39, FR-40 and FR-65
// Use case: UC 12, Emergency access and the audit log (the admin's audit view)
// Preconditions: A clinician is signed in on the on-site app with the patient on their panel. An admin is signed in.
// Inputs and system conditions: POST /patients/{id}/diagnoses and /patients/{id}/medications as the clinician; GET
//   /admin/audit?Patient={id} as the admin; on the on-site app.
// Test data: Patient Synthetic Audited values. Diagnoses "Essential (primary) hypertension" (I10) and "Generalized
//   anxiety disorder" (F41.1, sensitive by its code). Medication "sertraline 50 MG Oral Tablet", added unmarked, then
//   marked sensitive. Clinician "Dr. Sensitive Writes".
// Steps:
//   1. As the clinician, add the two diagnoses and the medication.
//   2. As the admin, open the audit log for the patient.
//   3. As the clinician, mark the medication sensitive.
//   4. As the admin, open the audit log for the patient again.
// Expected result: Step 2: the anxiety insert is listed with the clinician's name, the "Sensitive" tag and "A
//   sensitive item changed. Its values aren't shown here.", and "anxiety" appears nowhere on the page; nor, since step
//   18a, do "hypertension" or "sertraline", whose inserts aren't sensitive. The history copies say their values aren't
//   shown. Step 4: the medication's events are listed as a sensitive item's, by the same clinician, and "sertraline"
//   still appears nowhere.
// Creator: Tahseen Arnan
public class TC_UC12_07_SensitiveValuesHidden(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_audit_page_shows_that_a_sensitive_item_changed_and_never_its_values()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Sensitive Writes", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Synthetic", "Audited values");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(apps.OnPrem, clinician);
        using var hypertension = await SaveAsync(session.Client, patient, "diagnoses", null, ("Name", "Essential (primary) hypertension"), ("Status", "active"));
        using var anxiety = await SaveAsync(session.Client, patient, "diagnoses", null, ("Name", "Generalized anxiety disorder"), ("Status", "active"));
        using var sertraline = await SaveAsync(session.Client, patient, "medications", null, ("Name", "sertraline 50 MG Oral Tablet"), ("Status", "active"));
        var medication = await IdAsync(apps, "medication_statements", patient);
        var admin = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("The Values Reviewer", StaffRoles.Admin));

        var page = await admin.Client.GetStringAsync($"/admin/audit?Patient={patient}", Cancel);
        Assert.DoesNotContain("hypertension", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sertraline", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("anxiety", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("F41.1", page);
        Assert.Contains("<span class=\"sc-tag sc-tag--warning\">Sensitive</span> <span>A sensitive item changed. Its values aren't shown here.</span>", page);
        Assert.Contains("Dr. Sensitive Writes", page);
        Assert.Contains("A copy for the lists' history. Its values aren't shown here: see the entry's own event.", page);
        // One sensitive event so far: the anxiety diagnosis's insert.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "A sensitive item changed"));

        using var marked = await SaveAsync(session.Client, patient, "medications", medication,
            ("Name", "sertraline 50 MG Oral Tablet"), ("Status", "active"), ("Sensitive", "true"));

        var after = await admin.Client.GetStringAsync($"/admin/audit?Patient={patient}", Cancel);
        Assert.DoesNotContain("sertraline", after, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("anxiety", after, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hypertension", after, StringComparison.OrdinalIgnoreCase);
        // The anxiety insert, and the medication's insert and update.
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(after, "A sensitive item changed").Count);
        Assert.Contains($"medication_statements {medication}", Words(after));
    }
}
