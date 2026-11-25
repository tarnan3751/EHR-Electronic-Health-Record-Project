using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.ChartListTests;

namespace Ehr.Web.Tests.TestCases.UC12;

// Test Case ID: TC-UC12-09
// Purpose / scenario: The admin audit view shows who did what to which record, but no clinical text for any patient
//   data, sensitive or not (the expansion plan's step 18a): a change to the record is listed with who, the action, the
//   table and row, and the names of the fields it changed, never a value; the reason a clinician gave for emergency
//   access is still shown; an event about no patient, a desktop sign-in, keeps its details. The same off-site.
// Type: Functional (alternative flow)
// Requirements: FR-31, FR-32, proposed FR-40 and FR-65
// Use case: UC 12, Open a chart through emergency access (the admin's audit view)
// Preconditions: Dr. Named Fields has the patient on their panel; Dr. Called In doesn't; an admin, Field Reader.
// Inputs and system conditions: As Dr. Named Fields, POST /patients/{id}/diagnoses twice (add, then resolve) and sign in
//   from the desktop app; as Dr. Called In, POST /patients/{id}/reason; as the admin, GET /admin/audit?Patient={id} on
//   the on-site app and the off-site app, and GET /admin/audit?Actor={clinician}&Action=sign_in on-site.
// Test data: Patient Synthetic Named-fields. Diagnosis "Essential (primary) hypertension" (I10), added active, then
//   resolved. Reason "Synthetic: called in to cover the ward overnight." Desktop device "test desktop".
// Steps:
//   1. As Dr. Named Fields, add the diagnosis and resolve it.
//   2. As Dr. Called In, open the chart with the reason.
//   3. As the admin, open the patient's audit events on-site.
//   4. The same off-site.
//   5. As the admin, open Dr. Named Fields' sign-ins.
// Expected result: Step 3: the diagnosis's insert says "Fields changed: clinical_status, code, display, id, patient_id,
//   sensitive" and its update "Fields changed: clinical_status"; the emergency access's row lists its fields and the
//   break_glass event its reason; "hypertension" and "I10" appear nowhere on the page. Step 4: the same, and still no
//   "hypertension". Step 5: the desktop sign-in's details, with its device, are shown.
// Creator: Tahseen Arnan
public class TC_UC12_09_NoClinicalText(WebApps apps) : IClassFixture<WebApps>
{
    const string Reason = "Synthetic: called in to cover the ward overnight.";

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_audit_page_names_the_fields_a_change_to_patient_data_changed_and_never_a_value()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Named Fields", StaffRoles.Clinician);
        var colleague = await apps.CreateStaffAsync("Dr. Called In", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Synthetic", "Named-fields");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(apps.OnPrem, clinician);

        // 1.
        using (var added = await SaveAsync(session.Client, patient, "diagnoses", null, ("Name", "Essential (primary) hypertension"), ("Status", "active")))
        {
            await ReadAsync(added);
        }

        var diagnosis = await IdAsync(apps, "conditions", patient);
        using (var resolved = await SaveAsync(session.Client, patient, "diagnoses", diagnosis, ("Name", "Essential (primary) hypertension"), ("Status", "resolved")))
        {
            await ReadAsync(resolved);
        }

        await apps.DesktopSignInAsync(apps.OnPrem, clinician);

        // 2.
        var called = await SignInAsync(apps.OnPrem, colleague);
        using (var opened = await PostFormAsync(called.Client, $"/patients/{patient}/reason", $"/patients/{patient}/reason", ("Reason", Reason)))
        {
            await ReadAsync(opened);
        }

        // 3.
        var admin = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Field Reader", StaffRoles.Admin));
        var page = await admin.Client.GetStringAsync($"/admin/audit?Patient={patient}", Cancel);
        Assert.Contains("<span class=\"sc-muted\">Fields changed:</span> <span class=\"sc-table__id\" data-audit-fields>clinical_status, code, display, id, patient_id, sensitive</span>", page);
        Assert.Contains("<span class=\"sc-table__id\" data-audit-fields>clinical_status</span>", page);
        Assert.Contains("<span class=\"sc-table__id\" data-audit-fields>clinician_id, expires_at, granted_at, id, patient_id, reason</span>", page);
        Assert.Contains(Reason, page);
        Assert.Contains($"conditions {diagnosis}", Words(page));
        Assert.DoesNotContain("hypertension", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("I10", page);
        Assert.DoesNotContain("<pre>", page);

        // 4. One request off-site: the same page, read from the cloud's copy.
        var offSite = await Client(apps.Cloud, admin.Jar).GetStringAsync($"/admin/audit?Patient={patient}", Cancel);
        Assert.Contains("data-audit-fields>clinical_status, code, display, id, patient_id, sensitive</span>", offSite);
        Assert.Contains(Reason, offSite);
        Assert.DoesNotContain("hypertension", offSite, StringComparison.OrdinalIgnoreCase);

        // 5.
        var signIns = await admin.Client.GetStringAsync($"/admin/audit?Actor={clinician.Id}&Action={AuditActions.SignIn}", Cancel);
        Assert.Contains("<details><summary>Details</summary><pre>", signIns);
        Assert.Contains("test desktop", signIns);
    }
}
