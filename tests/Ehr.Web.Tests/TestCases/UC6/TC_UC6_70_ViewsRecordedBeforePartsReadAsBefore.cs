using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.RecordViewTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-70
// Purpose / scenario: Views recorded without their part, before step 13c2 or by an app older than its migration, read
//   as they did: from what the log says was read, with how many in a minute, "Record" for most pages and a count of
//   the notes a chart listed, which can't be told apart from notes opened.
// Type: Functional (alternative flow: views recorded before parts)
// Requirements: proposed FR-57
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a confirmed portal account, signed in on the on-site app, and has given another
//   patient of the clinic access without sensitive items. Dr. Before, a clinician, has had the patient on their panel
//   for two hours. The log holds, from Dr. Before, with no part: a read of the patient and of three signed notes in one
//   minute, and in an earlier minute a read of the patient's sensitive items.
// Inputs and system conditions: The patient and the proxy GET /portal/views; on the on-site app.
// Test data: None beyond the accounts.
// Steps:
//   1. The patient opens Who viewed my record.
//   2. The proxy opens Who viewed the record.
// Expected result: Step 1: Dr. Before's lines are, newest first, "3 visit notes", "Record", "Record, with sensitive
//   items". Step 2: "Record" alone.
// Creator: Tahseen Arnan
public class TC_UC6_70_ViewsRecordedBeforePartsReadAsBefore(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Views_with_no_part_read_as_they_did()
    {
        var before = await apps.CreateStaffAsync("Dr. Before", StaffRoles.Clinician);
        var patient = await ProxyTests.PersonAsync(apps, "Synthetic", "Unparted");
        await using (var connection = await apps.OpenDatabaseAsync())
        {
            await using var assign = new Npgsql.NpgsqlCommand(
                "INSERT INTO ehr.patient_assignments (id, patient_id, clinician_id, assigned_at) VALUES (gen_random_uuid(), @patient, @clinician, now() - interval '2 hours')",
                connection);
            assign.Parameters.AddWithValue("patient", patient.Patient);
            assign.Parameters.AddWithValue("clinician", before.Id);
            await assign.ExecuteNonQueryAsync(Cancel);
        }

        var (without, _) = await VisitSummaryTests.ProxyForAsync(apps, patient, "Partlesswithout", sensitive: false);
        await PartReadsAsync(apps, patient.Patient, before.Id, 40, (AuditActions.ReadSensitive, "patients", null));
        await PartReadsAsync(apps, patient.Patient, before.Id, 30,
            [(AuditActions.Read, "patients", null), .. Enumerable.Repeat((AuditActions.Read, "clinical_notes", (string?)null), 3)]);

        // Step 1.
        Assert.Equal(["3 visit notes", "Record", "Record, with sensitive items"], PartsOf(await PageAsync(patient.Client), "staff"));

        // Step 2.
        Assert.Equal(["Record"], PartsOf(await PageAsync(without.Client), "staff"));
    }
}
