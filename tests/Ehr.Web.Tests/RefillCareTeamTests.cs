using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RefillRequestTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests;

// Refill requests (step 13d), the review's findings that the test cases don't cover: a waiting request follows the care
// team as it changes, so whoever joins the panel answers it; a clinician locked for a while after failed sign-ins is on
// the care team still; and which medications have a request waiting doesn't depend on how many answered ones the page
// lists. The database's side of each is in Ehr.Security.Tests (RefillRequestSecurityTests).
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

        // Their item for this request is done and gone from their inbox. By this request's own link and rows, not by the
        // item's words: the fixture's clinician is shared, and another test's waiting request may sit in the same inbox.
        Assert.Equal([$"{clinician.Id} true"], await ItemsAsync(apps, patient.Patient, request));
        Assert.DoesNotContain($"href=\"{ReviewPath(patient.Patient, request)}\"", await session.Client.GetStringAsync("/inbox", Cancel));
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

    [Fact]
    public async Task A_medication_waiting_is_not_offered_however_many_answered_requests_are_newer()
    {
        var (clinician, _, patient) = await PortalPatientAsync(apps, "Busy");
        var waiting = await MedicationAsync(apps, patient.Patient);
        var answered = await MedicationAsync(apps, patient.Patient, "amlodipine 5 MG Oral Tablet", "197361");
        var app = apps.Database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);
        await RunAsync(app, patient.Account,
            "INSERT INTO ehr.refill_requests (id, patient_id, medication_statement_id, medication_display, requested_by, requested_at, status, sensitive) VALUES (gen_random_uuid(), @patient, @medication, '', gen_random_uuid(), now(), 'requested', false)",
            ("patient", patient.Patient), ("medication", waiting));
        for (var i = 0; i < 51; i++)
        {
            var request = Guid.CreateVersion7();
            await RunAsync(app, patient.Account,
                "INSERT INTO ehr.refill_requests (id, patient_id, medication_statement_id, medication_display, requested_by, requested_at, status, sensitive) VALUES (@id, @patient, @medication, '', gen_random_uuid(), now(), 'requested', false)",
                ("id", request), ("patient", patient.Patient), ("medication", answered));
            await RunAsync(app, clinician.Id, "UPDATE ehr.refill_requests SET decline_message = 'Synthetic: not now.' WHERE id = @id", ("id", request));
        }

        var html = await patient.Client.GetStringAsync(PortalPath, Cancel);
        // Lisinopril waits, older than the 51 answers: it isn't offered again, and its request is listed.
        Assert.DoesNotContain($"<span>{Lisinopril}", html);
        Assert.Contains("<span>amlodipine 5 MG Oral Tablet", html);
        Assert.Contains("Waiting for the care team to answer.", Said(html));
    }

    async Task LockAsync(Guid staff, string until)
    {
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand($"UPDATE ehr.staff_users SET lockout_end = {until} WHERE id = @id", db);
        command.Parameters.AddWithValue("id", staff);
        await command.ExecuteNonQueryAsync(Cancel);
    }

    // One transaction as the user, with the user context set first, as UserContextInterceptor sets it.
    static async Task RunAsync(string connectionString, Guid user, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Cancel);
        await using var transaction = await connection.BeginTransactionAsync(Cancel);
        await using (var set = new NpgsqlCommand("SELECT set_config('app.user_id', @user, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("user", user.ToString());
            await set.ExecuteNonQueryAsync(Cancel);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Cancel);
        await transaction.CommitAsync(Cancel);
    }
}
