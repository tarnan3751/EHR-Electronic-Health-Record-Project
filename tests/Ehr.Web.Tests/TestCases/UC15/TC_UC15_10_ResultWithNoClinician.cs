using Ehr.Data.Accounts;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.InboxTests;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.ResultTests;

namespace Ehr.Web.Tests.TestCases.UC15;

// Test Case ID: TC-UC15-10
// Purpose / scenario: A lab result no clinician can receive, its order's signer disabled and nobody with the patient on
//   their panel, reaches every administrator's inbox (the expansion plan's step 18a): "Result with no clinician", the
//   patient by ID and nothing of the test or its value, what the administrator can do, and no "Mark done", the mark
//   refused; the same off-site; a clinician's acknowledgment moves it under Done.
// Type: Functional (exceptional condition)
// Requirements: proposed FR-41, FR-45 and FR-66
// Use case: UC 15, Work through the inbox (and UC 20, Receive results from a lab)
// Preconditions: Dr. Gone Signer signed a complete blood count for the patient, then the patient left their panel and
//   their account was disabled; the administrator Result Watcher is signed in.
// Inputs and system conditions: the lab posts the result through its door, on the on-site app; GET /inbox as Result
//   Watcher on-site and off-site; POST /inbox?handler=Done for the item; Dr. Taking Over adds the patient to their panel
//   and acknowledges the results; GET /inbox and /inbox?done=true.
// Test data: Patient Synthetic Nobodys-result. Hemoglobin (718-7) 5.0 g/dL, below its range.
// Steps:
//   1. The lab files the result.
//   2. As Result Watcher, open the inbox on-site, then off-site; send "Mark done" for the item anyway.
//   3. As Dr. Taking Over, acknowledge the results; as Result Watcher, open the inbox and its Done view.
// Expected result: Step 2: "Result with no clinician", "Results are in, and no clinician can receive them", the patient's
//   ID and "Ask a clinician to add this patient to their patients and acknowledge the results."; neither the patient's
//   name, the test, its value, the order, "Abnormal" nor "Mark done"; off-site the same item; the mark is refused with
//   "Nothing to mark.". Step 3: "Nothing needs your action."; under Done, the item.
// Creator: Tahseen Arnan
public class TC_UC15_10_ResultWithNoClinician(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_result_no_clinician_can_receive_reaches_the_administrators_by_patient_id_alone()
    {
        var signer = await apps.CreateStaffAsync("Dr. Gone Signer", StaffRoles.Clinician);
        var watcherAccount = await apps.CreateStaffAsync("Result Watcher", StaffRoles.Admin);
        var patient = await apps.InsertPatientAsync("Synthetic", "Nobodys-result");
        await apps.AssignAsync(signer, patient);
        var signed = await SignInAsync(apps.OnPrem, signer);
        var order = await SignedAsync(signed.Client, patient, "lab", "Complete blood count (CBC)", Cbc);
        await LabTests.SettledAsync(apps, patient);
        await using (var connection = await apps.OpenDatabaseAsync())
        {
            await using var command = new NpgsqlCommand(
                """
                UPDATE ehr.patient_assignments SET ended_at = now() WHERE patient_id = @patient;
                UPDATE ehr.staff_users SET lockout_end = now() + interval '100 years' WHERE id = @signer;
                """, connection);
            command.Parameters.AddWithValue("patient", patient);
            command.Parameters.AddWithValue("signer", signer.Id);
            await command.ExecuteNonQueryAsync(Cancel);
        }

        // 1.
        DateTimeOffset collected = (await apps.Database.NowAsync()).AddMinutes(-15);
        using (var filed = await LabTests.PostAsync(LabTests.Client(apps.OnPrem), order, collected, (Hemoglobin, 5.0m, "g/dL")))
        {
            Assert.Equal(System.Net.HttpStatusCode.NoContent, filed.StatusCode);
        }

        // 2.
        var watcher = await SignInAsync(apps.OnPrem, watcherAccount);
        var inbox = await watcher.Client.GetStringAsync("/inbox", Cancel);
        var offSite = await Client(apps.Cloud, watcher.Jar).GetStringAsync("/inbox", Cancel);
        foreach (var page in new[] { inbox, offSite })
        {
            Assert.Contains("<span class=\"sc-tag\">Result with no clinician</span> Results are in, and no clinician can receive them", page);
            Assert.Contains($"<td class=\"sc-table__id\">{patient}</td>", page);
            Assert.Contains("Ask a clinician to add this patient to their patients and acknowledge the results.", page);
            foreach (var absent in new[] { "Nobodys", "Hemoglobin", "blood count", "5.0 g/dL", "Abnormal", "Mark done", order.ToString() })
            {
                Assert.DoesNotContain(absent, page, StringComparison.OrdinalIgnoreCase);
            }
        }

        var item = Guid.Parse((await EmergencyAccessFlows.QueryAsync(apps,
            "SELECT id::text FROM ehr.inbox_items WHERE recipient_id = @r AND source_id = @o", ("r", watcherAccount.Id), ("o", order))).Single());
        using (var refused = await MarkDoneAsync(watcher.Client, item, Token(inbox)))
        {
            Assert.Contains("Nothing to mark.", await ReadAsync(refused));
        }

        Assert.False(await IsDoneAsync(apps, item));

        // 3.
        var takingOver = await apps.CreateStaffAsync("Dr. Taking Over", StaffRoles.Clinician);
        await apps.AssignAsync(takingOver, patient);
        var clinician = await SignInAsync(apps.OnPrem, takingOver);
        using (var acknowledged = await AcknowledgeAsync(clinician.Client, patient, order))
        {
            await ReadAsync(acknowledged);
        }

        Assert.Contains("Nothing needs your action.", await watcher.Client.GetStringAsync("/inbox", Cancel));
        Assert.Contains("Results are in, and no clinician can receive them", await watcher.Client.GetStringAsync("/inbox?done=true", Cancel));
        Assert.True(await IsDoneAsync(apps, item));
    }
}
