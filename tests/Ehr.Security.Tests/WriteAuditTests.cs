using Ehr.Data.Accounts;
using Ehr.Testing;
using Xunit;

namespace Ehr.Security.Tests;

// db/policies/40-audit.sql: every write to a PHI table leaves an audit row with the actor from app.user_id, the row,
// its patient, and what changed, even though the actor's own role has nothing to do with the log.
public class WriteAuditTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    [Fact]
    public async Task Inserts_and_updates_are_recorded_with_the_actor_and_the_change()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);

        await Accounts.ExecuteAsync(database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword), clinician,
            "UPDATE ehr.patients SET family_name = 'Changed', version = 2 WHERE id = @id", ("id", patient));

        var events = await Accounts.QueryAsync(database.SuperuserConnectionString, null,
            """
            SELECT action || ' ' || table_name || ' ' || actor_id || ' ' || patient_id || ' ' || source || ' ' || details::text
            FROM ehr.audit_events WHERE row_id = @id ORDER BY id
            """,
            ("id", patient));

        Assert.Equal(2, events.Count);
        Assert.StartsWith($"insert patients {clinician} {patient} onprem ", events[0]);
        Assert.Contains("\"inserted\"", events[0]);
        Assert.Contains("\"family_name\": \"Patient\"", events[0]);
        Assert.StartsWith($"update patients {clinician} {patient} onprem ", events[1]);
        Assert.Contains("\"family_name\": {\"new\": \"Changed\", \"old\": \"Patient\"}", events[1]);
        Assert.Contains("\"version\": {\"new\": 2, \"old\": 1}", events[1]);
        // Columns that didn't change are not in the record.
        Assert.DoesNotContain("given_name", events[1]);
    }

    [Fact]
    public async Task A_write_without_a_user_is_recorded_with_no_actor()
    {
        // The superuser bypasses row-level security, as a migration or a seed would; the trigger still fires.
        var id = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            """
            INSERT INTO ehr.patients (id, mrn, given_name, family_name, birth_date, gender, version, updated_at)
            VALUES (@id, @mrn, 'Seeded', 'Patient', '1980-01-01', 'unknown', 1, now())
            """,
            ("id", id), ("mrn", id.ToString("N")[..12]));

        var actors = await Accounts.QueryAsync(database.SuperuserConnectionString, null,
            "SELECT coalesce(actor_id::text, 'none') FROM ehr.audit_events WHERE row_id = @id", ("id", id));

        Assert.Equal(["none"], actors);
    }
}
