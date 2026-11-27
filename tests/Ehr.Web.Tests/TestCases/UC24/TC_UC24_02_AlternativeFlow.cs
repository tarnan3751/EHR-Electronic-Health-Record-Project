using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.EmergencyAccessFlows;

namespace Ehr.Web.Tests.TestCases.UC24;

// Test Case ID: TC-UC24-02
// Purpose / scenario: The other ways through a review (the expansion plan's step 18). An access still in force is
//   shown, but can't be reviewed yet. One reviewed as not appropriate is found again by the audit page's "Not
//   appropriate", and those still waiting by "Waiting for review". And an access granted before this step, which no
//   inbox was given, reaches every administrator's inbox at the migration job's next run.
// Type: Functional (alternative flow)
// Requirements: proposed FR-64, FR-06
// Use case: UC 24, Review an emergency access
// Preconditions: Dr. Early, Dr. Late and Dr. Before, clinicians, each with an emergency access; the administrator Audit
//   Reviewer, on the on-site app.
// Inputs and system conditions: /admin/emergency-access/{id} for an access in force; the same ended, reviewed "Not
//   appropriate"; /admin/audit?Review=not-appropriate and ?Review=waiting; an access written with the database's
//   triggers off, then the step's db/policies run again.
// Test data: Patients "Synthetic Reviewedchart". Note "Synthetic: checked the rota, the clinician was covering the ward."
// Steps:
//   1. Open Dr. Early's access while it is in force.
//   2. End it; review it as "Not appropriate".
//   3. Filter the audit page by "Not appropriate", then by "Waiting for review".
//   4. Write Dr. Before's access with no items; run the policies; open the administrator's inbox.
// Expected result: Step 1: "In force until …", and no form. Step 2: saved, tagged "Not appropriate". Step 3: the first filter
//   lists Dr. Early's access and not Dr. Late's; the second lists Dr. Late's and not Dr. Early's. Step 4: the access is in
//   the inbox, once, after the run, and not before.
// Creator: Tahseen Arnan
public class TC_UC24_02_AlternativeFlow(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task An_access_in_force_waits_a_not_appropriate_one_is_found_again_and_an_older_one_reaches_the_inbox()
    {
        var admin = await apps.CreateStaffAsync("Audit Reviewer", StaffRoles.Admin);
        var reviewer = await SignInAsync(apps.OnPrem, admin);
        var early = await GrantAsync(apps, "Dr. Early");
        var late = await GrantAsync(apps, "Dr. Late");

        // 1.
        var inForce = await reviewer.Client.GetStringAsync($"/admin/emergency-access/{early.Id}", Cancel);
        Assert.Matches("In force until [0-9-]+ [0-9:]+ UTC\\.</span> It can be marked reviewed once it ends", inForce);
        Assert.DoesNotContain("data-review-form", inForce);

        // 2.
        await EndAsync(apps, early.Id);
        using (var reviewed = await ReviewAsync(reviewer.Client, early.Id, EmergencyAccessOutcomes.NotAppropriate, Note))
        {
            Assert.Contains("<span class=\"sc-tag sc-tag--danger\">Not appropriate</span> Reviewed by Audit Reviewer", await ReadAsync(reviewed));
        }

        // 3.
        var notAppropriate = await reviewer.Client.GetStringAsync("/admin/audit?Review=not-appropriate", Cancel);
        Assert.Contains($"/admin/emergency-access/{early.Id}", notAppropriate);
        Assert.DoesNotContain($"/admin/emergency-access/{late.Id}", notAppropriate);
        Assert.Contains("<span class=\"sc-tag sc-tag--danger\">Not appropriate</span>", notAppropriate);
        var waiting = await reviewer.Client.GetStringAsync("/admin/audit?Review=waiting", Cancel);
        Assert.Contains($"/admin/emergency-access/{late.Id}", waiting);
        Assert.DoesNotContain($"/admin/emergency-access/{early.Id}", waiting);

        // 4.
        var before = await apps.CreateStaffAsync("Dr. Before", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Synthetic", "Reviewedchart");
        var older = Guid.CreateVersion7();
        await using (var db = await apps.OpenDatabaseAsync())
        {
            await using var transaction = await db.BeginTransactionAsync(Cancel);
            await using var insert = new Npgsql.NpgsqlCommand(
                """
                SET LOCAL session_replication_role = replica;
                INSERT INTO ehr.break_glass_accesses (id, patient_id, clinician_id, reason, granted_at, expires_at)
                VALUES (@id, @patient, @clinician, 'Synthetic: before step 18', now() - interval '3 days', now() - interval '3 days' + interval '4 hours')
                """, db, transaction);
            insert.Parameters.AddWithValue("id", older);
            insert.Parameters.AddWithValue("patient", patient);
            insert.Parameters.AddWithValue("clinician", before.Id);
            await insert.ExecuteNonQueryAsync(Cancel);
            await transaction.CommitAsync(Cancel);
        }

        Assert.DoesNotContain($"/admin/emergency-access/{older}", await reviewer.Client.GetStringAsync("/inbox", Cancel));
        await SweepAsync(apps);
        var inbox = await reviewer.Client.GetStringAsync("/inbox", Cancel);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(inbox, $"/admin/emergency-access/{older}"));
    }
}
