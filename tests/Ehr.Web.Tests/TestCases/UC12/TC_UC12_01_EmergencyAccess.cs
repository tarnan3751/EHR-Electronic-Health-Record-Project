using System.Net;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC12;

// Test Case ID: TC-UC12-01
// Purpose / scenario: A chart off the clinician's panel asks for a reason before it shows anything, opens once one is
//   given, stays open to that clinician for four hours without asking again, and asks again once those hours are over.
//   Each use is flagged for an admin's review on the audit page.
// Type: Functional (successful flow)
// Requirements: FR-06
// Use case: UC 12, Open a chart through emergency access
// Preconditions: A clinician who has finished setup, an admin, and two patients: one on the clinician's panel and one
//   off it, with no emergency access given yet.
// Inputs and system conditions: On-site (the on-prem app). GET /patients/{id} for each patient; POST the reason to
//   /patients/{id}/reason; then, as the admin, GET /admin/audit with "Emergency access only" ticked and the patient's ID.
// Test data: Clinician "Dr. Night Cover", admin "Audit Reviewer", patients "Ona Paneled" (on the panel) and "Ezra
//   Offpanel" (off it). Reason: "Covering the night shift; patient called with chest pain."
// Steps:
//   1. Open the chart of the patient on the panel.
//   2. Open the chart of the patient off the panel.
//   3. Give the reason and open the chart.
//   4. Open the chart again.
//   5. As the admin, show the emergency accesses for the patient.
//   6. Move the access's end into the past (as the database superuser, standing in for four hours passing), then open
//      the chart again.
// Expected result: Step 1 shows the chart with no reason asked and no break_glass event. Step 2 is sent to
//   /patients/{id}/reason, which doesn't show the patient's name, and nothing is read. Step 3 shows the chart with the
//   "Emergency access" notice, and the grant ends four hours after it was given. Step 4 shows the chart without asking.
//   Step 5 lists one break_glass event, shown as "Emergency access", marked Review, with the clinician's name, the
//   patient's ID and the reason. Step 6 is sent to the reason page again.
// Creator: Luke Garcia
public class TC_UC12_01_EmergencyAccess(WebApps apps) : IClassFixture<WebApps>
{
    const string Reason = "Covering the night shift; patient called with chest pain.";

    [Fact]
    public async Task A_chart_off_the_panel_asks_for_a_reason_opens_for_four_hours_and_is_flagged_for_review()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Night Cover", StaffRoles.Clinician);
        var admin = await apps.CreateStaffAsync("Audit Reviewer", StaffRoles.Admin);
        var paneled = await apps.InsertPatientAsync("Ona", "Paneled");
        var patient = await apps.InsertPatientAsync("Ezra", "Offpanel");
        await apps.AssignAsync(clinician, paneled);
        var session = await SignInAsync(apps.OnPrem, clinician);
        var direct = Client(apps.OnPrem, session.Jar, followRedirects: false);

        // 1. On the panel: no reason asked.
        using (var own = await direct.GetAsync($"/patients/{paneled}", Cancel))
        {
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
            Assert.DoesNotContain("Emergency access", await own.Content.ReadAsStringAsync(Cancel));
        }

        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.BreakGlass, clinician.Id, paneled));

        // 2. Off the panel: the reason page, before anything about the patient is read or shown.
        using (var chart = await direct.GetAsync($"/patients/{patient}", Cancel))
        {
            Assert.Equal(HttpStatusCode.Redirect, chart.StatusCode);
            Assert.Equal($"/patients/{patient}/reason", new Uri(Base, chart.Headers.Location!).AbsolutePath);
        }

        var reasonHtml = await session.Client.GetStringAsync($"/patients/{patient}/reason", Cancel);
        Assert.Contains("Not one of your patients. To open the chart, say why you need it", reasonHtml);
        Assert.DoesNotContain("Offpanel", reasonHtml);
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient));

        // 3. The reason opens the chart, for four hours.
        using (var opened = await PostFormAsync(session.Client, $"/patients/{patient}/reason", $"/patients/{patient}/reason", ("Reason", Reason)))
        {
            var html = await ReadAsync(opened);
            Assert.Equal($"/patients/{patient}", PathOf(opened));
            Assert.Contains("Offpanel, Ezra", html);
            Assert.Contains("Emergency access. This isn't one of your patients.", Words(html));
        }

        Assert.Equal(TimeSpan.FromHours(4), await AccessLengthAsync(clinician.Id, patient));

        // 4. Open again without asking.
        using (var again = await direct.GetAsync($"/patients/{patient}", Cancel))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }

        // 5. Flagged for the admin's review, with who, which patient and why.
        var adminSession = await SignInAsync(apps.OnPrem, admin);
        var audit = await ReadAsync(await adminSession.Client.GetAsync($"/admin/audit?Flagged=true&Patient={patient}", Cancel));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.BreakGlass, clinician.Id, patient));
        Assert.Contains("<tr class=\"sc-table__flagged\">", audit);
        Assert.Contains("Emergency access <span class=\"sc-tag sc-tag--danger\">Review</span>", audit);
        Assert.Contains("Dr. Night Cover", audit);
        Assert.Contains(patient.ToString(), audit);
        Assert.Contains(Reason, audit);

        // 6. Once the four hours are over, the reason is asked for again.
        await EndAccessAsync(clinician.Id, patient);
        using (var expired = await direct.GetAsync($"/patients/{patient}", Cancel))
        {
            Assert.Equal(HttpStatusCode.Redirect, expired.StatusCode);
            Assert.Equal($"/patients/{patient}/reason", new Uri(Base, expired.Headers.Location!).AbsolutePath);
        }
    }

    // How long the clinician's grant to the patient lasts, from the stored row.
    async Task<TimeSpan> AccessLengthAsync(Guid clinicianId, Guid patientId)
    {
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "SELECT expires_at - granted_at FROM ehr.break_glass_accesses WHERE clinician_id = @clinician AND patient_id = @patient", db);
        command.Parameters.AddWithValue("clinician", clinicianId);
        command.Parameters.AddWithValue("patient", patientId);
        return (TimeSpan)(await command.ExecuteScalarAsync(Cancel))!;
    }

    // Stands in for four hours passing: the grant ended a minute ago.
    async Task EndAccessAsync(Guid clinicianId, Guid patientId)
    {
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE ehr.break_glass_accesses SET expires_at = now() - interval '1 minute' WHERE clinician_id = @clinician AND patient_id = @patient", db);
        command.Parameters.AddWithValue("clinician", clinicianId);
        command.Parameters.AddWithValue("patient", patientId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(Cancel));
    }
}
