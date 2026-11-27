using System.Net;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// A check-in the database refuses (step 9c's follow-up, after its second review): the appointment page answers with its
// "Not saved" and the appointment stays booked, with no visit, never an error page. The database's own rule for a visit
// the apps start refuses only a status the page never sends, so the refusal is forced here, in this class's own
// database, with a trigger of the test's that refuses this patient's visits as that rule would (23514).
public class CheckInRefusedTests(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_check_in_the_database_refuses_is_not_saved_and_the_appointment_stays_booked()
    {
        var (clinician, patient, session) = await AppointmentTests.ClinicianAsync(apps, "Dr. Check-In Refused");
        using var booked = await AppointmentTests.BookAsync(session.Client, patient, clinician.Id, AppointmentTests.Day(5), "09:00", "Synthetic: check-up");
        var appointment = await AppointmentTests.IdAsync(apps, patient);
        await using (var db = await apps.OpenDatabaseAsync())
        {
            await using var refuse = new NpgsqlCommand(
                $"""
                CREATE FUNCTION ehr.test_refuse_visit() RETURNS trigger LANGUAGE plpgsql
                    AS $$ BEGIN RAISE EXCEPTION 'Synthetic: refused' USING ERRCODE = '23514'; END $$;
                CREATE TRIGGER encounters_test_refuse BEFORE INSERT ON ehr.encounters
                    FOR EACH ROW WHEN (NEW.patient_id = '{patient}') EXECUTE FUNCTION ehr.test_refuse_visit();
                """, db);
            await refuse.ExecuteNonQueryAsync(Cancel);
        }

        using var checkedIn = await AppointmentTests.ChangeAsync(session.Client, appointment, "CheckIn");

        Assert.Equal(HttpStatusCode.Conflict, checkedIn.StatusCode);
        Assert.Contains("Not saved.", Words(await checkedIn.Content.ReadAsStringAsync(Cancel)));
        Assert.Equal(["booked none"], await ProxyTests.QueryAsync(apps,
            "SELECT status || ' ' || coalesce(encounter_id::text, 'none') FROM ehr.appointments WHERE patient_id = @patient", patient));
        Assert.Equal(["0"], await ProxyTests.QueryAsync(apps, "SELECT count(*)::text FROM ehr.encounters WHERE patient_id = @patient", patient));
    }
}
