using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC12;

// Test Case ID: TC-UC12-04
// Purpose / scenario: Everything done under emergency access is in the audit log, under the clinician's account: the
//   grant with its reason, the row the grant wrote, every view of the chart, and a change made to the patient's record
//   from it. The admin's audit page, filtered by the patient, lists the same events.
// Type: Functional (exceptional condition)
// Requirements: FR-32, with FR-06
// Use case: UC 12, Open a chart through emergency access
// Preconditions: A clinician who has finished setup, an admin, and a patient off the clinician's panel.
// Inputs and system conditions: On-site (the on-prem app). POST the reason; GET the chart once more; POST "Add to my
//   patients" from the chart; then, as the database superuser, SELECT the patient's events from ehr.audit_events, and,
//   as the admin, GET /admin/audit with the patient's ID.
// Test data: Clinician "Dr. Logged Access", admin "Log Reader", patient "Tobin Recorded". Reason: "Paged by the ward;
//   the patient's own doctor is away."
// Steps:
//   1. Give the reason; the chart opens (view 1).
//   2. Open the chart again (view 2).
//   3. Choose "Add to my patients" on the chart (view 3, then the change, then view 4 as the chart shows again).
//   4. Read the patient's audit events, and the admin's audit page for the patient.
// Expected result: Apart from the test's own insert of the patient, every one of the patient's events is the
//   clinician's: one break_glass, with the reason, for the break_glass_accesses row; one insert of that
//   break_glass_accesses row; four reads; one insert of a patient_assignments row; and, since the expansion plan's step
//   18, an insert of an inbox item for each of the two administrators, the access to review. Nothing else. The admin's page lists
//   break_glass, insert and read events for the patient, with the reason.
// Creator: Luke Garcia
public class TC_UC12_04_EverythingAudited(WebApps apps) : IClassFixture<WebApps>
{
    const string Reason = "Paged by the ward; the patient's own doctor is away.";

    [Fact]
    public async Task The_grant_every_view_and_every_change_under_emergency_access_are_logged()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Logged Access", StaffRoles.Clinician);
        var admin = await apps.CreateStaffAsync("Log Reader", StaffRoles.Admin);
        var patient = await apps.InsertPatientAsync("Tobin", "Recorded");
        var session = await SignInAsync(apps.OnPrem, clinician);

        // 1 and 2.
        using (var opened = await PostFormAsync(session.Client, $"/patients/{patient}/reason", $"/patients/{patient}/reason", ("Reason", Reason)))
        {
            await ReadAsync(opened);
        }

        using var again = await session.Client.GetAsync($"/patients/{patient}", Cancel);
        await ReadAsync(again);

        // 3. A change, from the chart already open.
        using (var added = await PostFormAsync(session.Client, $"/patients/{patient}?handler=Assign", again))
        {
            Assert.Contains("Remove from my patients", await ReadAsync(added));
        }

        // 4. The log, as stored: everything but the test's own insert of the patient, which has no actor.
        var events = await EventsAsync(patient);
        Assert.Single(events, e => e.Actor is null && e.Action == AuditActions.Insert && e.Table == "patients");
        events.RemoveAll(e => e.Actor is null && e.Action == AuditActions.Insert && e.Table == "patients");
        Assert.All(events, e => Assert.Equal<Guid?>(clinician.Id, e.Actor));
        List<(string Action, string? Table, int Count)> expected =
        [
            (AuditActions.BreakGlass, "break_glass_accesses", 1),
            (AuditActions.Insert, "break_glass_accesses", 1),
            // Step 18: the access goes to each administrator's inbox to review, the bootstrap admin's and Log Reader's,
            // which the database writes in the same transaction, under the clinician who granted it.
            (AuditActions.Insert, "inbox_items", 2),
            (AuditActions.Insert, "patient_assignments", 1),
            (AuditActions.Read, "patients", 4),
        ];
        Assert.Equal(expected, events
            .GroupBy(e => (e.Action, e.Table))
            .Select(g => (g.Key.Action, g.Key.Table, Count: g.Count()))
            .OrderBy(g => g.Action, StringComparer.Ordinal).ThenBy(g => g.Table, StringComparer.Ordinal)
            .ToList());
        Assert.Equal(Reason, Assert.Single(events, e => e.Action == AuditActions.BreakGlass).Reason);

        // And as the admin reviews it.
        var adminSession = await SignInAsync(apps.OnPrem, admin);
        var audit = await ReadAsync(await adminSession.Client.GetAsync($"/admin/audit?Patient={patient}", Cancel));
        Assert.Contains("Break the glass <span class=\"sc-tag sc-tag--danger\">Review</span>", audit);
        Assert.Contains("<td>Insert</td>", audit);
        Assert.Contains("<td>Read</td>", audit);
        Assert.Contains("Paged by the ward; the patient&#x27;s own doctor is away.", audit);
    }

    // Every audit event for the patient, read as the superuser.
    async Task<List<(Guid? Actor, string Action, string? Table, string? Reason)>> EventsAsync(Guid patientId)
    {
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "SELECT actor_id, action, table_name, reason FROM ehr.audit_events WHERE patient_id = @patient ORDER BY id", db);
        command.Parameters.AddWithValue("patient", patientId);
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        var events = new List<(Guid?, string, string?, string?)>();
        while (await reader.ReadAsync(Cancel))
        {
            events.Add((
                reader.IsDBNull(0) ? null : reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return events;
    }
}
