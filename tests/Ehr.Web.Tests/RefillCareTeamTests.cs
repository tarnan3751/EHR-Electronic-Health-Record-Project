using Ehr.Data.Accounts;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RefillRequestTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests;

// Refill requests (step 13d), the review's findings that the test cases don't cover: a waiting request follows the care
// team as it changes, so whoever joins the panel answers it; a clinician locked for a while after failed sign-ins is on
// the care team still. The database's side of each is in Ehr.Security.Tests (RefillRequestSecurityTests).
public class RefillCareTeamTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_clinician_who_joins_after_the_care_team_left_answers_the_waiting_request()
    {
        var (clinician, session, patient) = await PortalPatientAsync(apps, "Handedover");
        await MedicationAsync(apps, patient.Patient);
        using (var asked = await AskAsync(patient.Client, Lisinopril))
        {
            Assert.Equal(PortalPath, PathOf(asked));
        }

        var request = await RequestIdAsync(apps, patient.Patient);
        // Dr. Test takes the patient off their panel, from the chart.
        using (var removed = await PostFormAsync(session.Client, $"/patients/{patient.Patient}?handler=Unassign", $"/patients/{patient.Patient}"))
        {
            Assert.True(removed.IsSuccessStatusCode);
        }

        Assert.DoesNotContain("A patient asked to renew a medication", Said(await session.Client.GetStringAsync("/inbox", Cancel)));
        // On the patient's page nothing changes: the request waits.
        Assert.Contains("Waiting for the care team to answer.", Said(await patient.Client.GetStringAsync(PortalPath, Cancel)));

        // Another clinician opens the chart, giving the reason it asks of someone off the panel, and puts the patient on
        // their panel from it.
        var joining = await apps.CreateStaffAsync("Dr. Joining", StaffRoles.Clinician);
        var joined = await SignInAsync(apps.OnPrem, joining);
        using (var opened = await PostFormAsync(joined.Client, $"/patients/{patient.Patient}/reason", $"/patients/{patient.Patient}/reason",
            ("Reason", "Synthetic: taking over this patient's care.")))
        {
            Assert.Equal($"/patients/{patient.Patient}", PathOf(opened));
        }

        using (var added = await PostFormAsync(joined.Client, $"/patients/{patient.Patient}?handler=Assign", $"/patients/{patient.Patient}"))
        {
            Assert.True(added.IsSuccessStatusCode);
        }

        Assert.Contains($"{joining.Id} false", await ItemsAsync(apps, patient.Patient, request));
        var inbox = await joined.Client.GetStringAsync("/inbox", Cancel);
        Assert.Contains($"href=\"{ReviewPath(patient.Patient, request)}\"", inbox);
        using (var declined = await DeclineAsync(joined.Client, patient.Patient, request, "Synthetic: I'm your new doctor; please book a visit."))
        {
            Assert.Contains("Declined:", Said(await ReadAsync(declined)));
        }

        Assert.All(await ItemsAsync(apps, patient.Patient, request), item => Assert.EndsWith(" true", item));
    }

    [Fact]
    public async Task A_clinician_locked_for_a_while_is_on_the_care_team_and_a_disabled_one_is_not()
    {
        var (clinician, _, patient) = await PortalPatientAsync(apps, "Locked");
        await MedicationAsync(apps, patient.Patient);
        await LockAsync(clinician.Id, "now() + interval '15 minutes'");

        var page = Said(await patient.Client.GetStringAsync(PortalPath, Cancel));
        Assert.DoesNotContain("No care team yet:", page);
        using (var asked = await AskAsync(patient.Client, Lisinopril))
        {
            Assert.Equal(PortalPath, PathOf(asked));
        }

        Assert.Equal([$"{clinician.Id} false"], await ItemsAsync(apps, patient.Patient, await RequestIdAsync(apps, patient.Patient)));

        await LockAsync(clinician.Id, "now() + interval '100 years'");
        Assert.Contains("No care team yet:", Said(await patient.Client.GetStringAsync(PortalPath, Cancel)));
        await LockAsync(clinician.Id, "NULL");
    }

    async Task LockAsync(Guid staff, string until)
    {
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand($"UPDATE ehr.staff_users SET lockout_end = {until} WHERE id = @id", db);
        command.Parameters.AddWithValue("id", staff);
        await command.ExecuteNonQueryAsync(Cancel);
    }
}
