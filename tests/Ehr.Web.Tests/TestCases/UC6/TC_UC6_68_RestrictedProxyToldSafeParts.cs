using Ehr.Data.Audit;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.RecordViewTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-68
// Purpose / scenario: Someone the patient shares their record with, without sensitive items, is told which part of the
//   record a care-team member viewed only when that can't hint at a change or an item kept from them: the chart and the
//   vital signs, but not the lists, orders, who the record is shared with, or a note being written, whose views are "The
//   record" to them, as every view was before parts were recorded, and are one line with how many, as views of one
//   part in one minute are. Which lines they see is unchanged.
// Type: Functional (alternative flow: proxies)
// Requirements: proposed FR-57, FR-19
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a confirmed portal account, signed in on the on-site app, and has given another patient
//   of the clinic access with messages and without sensitive items. Dr. Test has had the patient on their panel for two
//   hours. The log holds, in one minute ten minutes ago, Dr. Test's views of the chart, the vital signs, the allergies'
//   form, the order form, the proxies' page and dictation, a second apart, in that order.
// Inputs and system conditions: The patient and the proxy GET /portal/views; on the on-site app.
// Test data: None beyond the accounts.
// Steps:
//   1. The patient opens Who viewed my record.
//   2. The proxy opens Who viewed the record.
// Expected result: Step 1: Dr. Test's lines are, newest first, "A visit note, being written", "Who you share your record
//   with", "Your orders for tests and medicines", "Your allergies, medications and diagnoses", "Your vital signs", "Your
//   chart, with the list of your visit notes". Step 2: "The record, 4 times", "The vital signs", "The chart, with
//   the list of visit notes".
// Creator: Tahseen Arnan
public class TC_UC6_68_RestrictedProxyToldSafeParts(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_proxy_without_sensitive_items_is_told_only_the_parts_that_hint_at_nothing()
    {
        var (clinician, _, patient) = await ViewedPatientAsync(apps, "Restricted");
        var (without, _) = await VisitSummaryTests.ProxyForAsync(apps, patient, "Toldwithout", sensitive: false, scope: "record-and-messages");
        // On the panel for the two hours before the assignment the test began with, so the views below are the care team's.
        await using (var connection = await apps.OpenDatabaseAsync())
        {
            await using var assign = new NpgsqlCommand(
                """
                INSERT INTO ehr.patient_assignments (id, patient_id, clinician_id, assigned_at, ended_at)
                VALUES (gen_random_uuid(), @patient, @clinician, now() - interval '2 hours', now() - interval '1 minute')
                """, connection);
            assign.Parameters.AddWithValue("patient", patient.Patient);
            assign.Parameters.AddWithValue("clinician", clinician.Id);
            await assign.ExecuteNonQueryAsync(Cancel);
        }

        await PartReadsAsync(apps, patient.Patient, clinician.Id, 10,
            [.. new[] { AuditParts.Chart, AuditParts.Vitals, AuditParts.Lists, AuditParts.Orders, AuditParts.Access, AuditParts.NoteDraft }
                .Select(part => (AuditActions.Read, "patients", (string?)part))]);

        // Step 1.
        Assert.Equal(
            [
                "A visit note, being written", "Who you share your record with", "Your orders for tests and medicines", "Your allergies, medications and diagnoses",
                "Your vital signs", "Your chart, with the list of your visit notes",
            ],
            PartsOf(await PageAsync(patient.Client), "staff"));

        // Step 2.
        Assert.Equal(["The record, 4 times", "The vital signs", "The chart, with the list of visit notes"], PartsOf(await PageAsync(without.Client), "staff"));
    }
}
