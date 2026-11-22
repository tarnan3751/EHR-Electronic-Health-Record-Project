using System.Net;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The expansion plan's step 13c2: each page and API that reads a patient's record records which part of the record it
// was (AuditParts) on every read it makes, each kind of read with its own table. Each case opens one page, or calls one
// API, as the account that would, for a patient of its own, and reads back the reads that request recorded: by this
// account, of this patient, from the moment before it was sent.
public class AuditPartTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // A patient signed in to the portal, on the panel of a clinician of their own, who is signed in: so the clinician's
    // panel, day and inbox hold this patient alone.
    async Task<(StaffAccount Clinician, Session Session, ProxyTests.Person Patient)> PatientAsync()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Parts", StaffRoles.Clinician);
        var person = await ProxyTests.PersonAsync(apps, "Synthetic", "Parted");
        var session = await SignInAsync(apps.OnPrem, clinician);
        await RecordViewTests.AssignNowAsync(apps, clinician, person.Patient);
        return (clinician, session, person);
    }

    // "table part" for each read recorded by the account, of the patient, since the moment given: "none" for a read with
    // no part.
    async Task<List<string>> ReadsAsync(Guid patient, Guid actor, DateTimeOffset since)
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT DISTINCT coalesce(table_name, '-') || ' ' || coalesce(part, 'none') FROM ehr.audit_events
            WHERE patient_id = @patient AND actor_id = @actor AND action = ANY(@reads) AND occurred_at >= @since
            ORDER BY 1
            """, connection);
        command.Parameters.AddWithValue("patient", patient);
        command.Parameters.AddWithValue("actor", actor);
        command.Parameters.AddWithValue("reads", AuditActions.Reads);
        command.Parameters.AddWithValue("since", since);
        var reads = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        while (await reader.ReadAsync(Cancel))
        {
            reads.Add(reader.GetString(0));
        }

        return reads;
    }

    // As the clinician, who books and is sent the item.
    async Task ExecuteAsync(Guid user, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var transaction = await connection.BeginTransactionAsync(Cancel);
        await using (var context = new NpgsqlCommand("SELECT set_config('app.user_id', @user, true)", connection, transaction))
        {
            context.Parameters.AddWithValue("user", user.ToString());
            await context.ExecuteNonQueryAsync(Cancel);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Cancel);
        await transaction.CommitAsync(Cancel);
    }

    // The clinic's pages for one patient, and the portal's, each with the reads it records. {0} is the patient.
    [Theory]
    [InlineData("clinic", "/patients/{0}", "patients chart")]
    [InlineData("clinic", "/patients/{0}/allergies", "patients lists")]
    [InlineData("clinic", "/patients/{0}/chart-history", "patients lists")]
    [InlineData("clinic", "/patients/{0}/orders/new", "patients orders")]
    [InlineData("clinic", "/patients/{0}/orders/discontinued", "patients orders")]
    [InlineData("clinic", "/patients/{0}/vitals", "patients vitals")]
    [InlineData("clinic", "/patients/{0}/appointments/new", "patients appointments")]
    [InlineData("clinic", "/patients/{0}/messages/new", "patients conversations")]
    [InlineData("clinic", "/patients/{0}/proxies", "patients access")]
    [InlineData("clinic", "/patients/{0}/dictate", "patients note-draft")]
    [InlineData("clinic", "/patients", "patients panel")]
    [InlineData("clinic", "/patients?name=Parted", "patients search")]
    [InlineData("clinic", "/fhir/Patient/{0}", "patients fhir")]
    [InlineData("clinic", "/patients/{0}/forms", "patients forms")]
    [InlineData("portal", "/portal", "patients home")]
    [InlineData("portal", "/portal/appointments", "patients appointments")]
    [InlineData("portal", "/portal/results", "patients results")]
    [InlineData("portal", "/portal/messages", "patients conversations")]
    [InlineData("portal", "/portal/summaries", "patients summaries")]
    [InlineData("portal", "/portal/access", "patients access")]
    [InlineData("portal", "/portal/views", "audit_events views")]
    [InlineData("portal", "/portal/refills", "patients refills")]
    [InlineData("portal", "/portal/forms", "patients forms")]
    [InlineData("portal", RecordDownload.FilePath, "patients download")]
    [InlineData("portal", RecordDownload.PrintPath, "patients printable")]
    public async Task Each_page_records_its_part_on_every_read(string site, string path, params string[] reads)
    {
        var (clinician, session, patient) = await PatientAsync();
        var (client, actor) = site == "clinic" ? (session.Client, clinician.Id) : (patient.Client, patient.Account);
        var since = DateTimeOffset.UtcNow;

        using var response = await client.GetAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, path, patient.Patient), Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(reads, await ReadsAsync(patient.Patient, actor, since));
    }

    // A clinician's day and inbox name the patient: each a read of the patient, as part of that list.
    [Fact]
    public async Task The_day_and_the_inbox_record_their_parts()
    {
        var (clinician, session, patient) = await PatientAsync();
        await ExecuteAsync(clinician.Id,
            """
            INSERT INTO ehr.appointments (id, patient_id, clinician_id, starts_at, minutes, reason, status)
            VALUES (gen_random_uuid(), @patient, @clinician, now() + interval '1 minute', 15, 'Synthetic follow-up', 'booked');
            INSERT INTO ehr.inbox_items (id, recipient_id, kind, patient_id, summary, created_at)
            VALUES (gen_random_uuid(), @clinician, 'message', @patient, 'Synthetic message', now());
            """, ("patient", patient.Patient), ("clinician", clinician.Id));

        var since = DateTimeOffset.UtcNow;
        Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync("/schedule", Cancel)).StatusCode);
        Assert.Equal(["patients schedule"], await ReadsAsync(patient.Patient, clinician.Id, since));

        since = DateTimeOffset.UtcNow;
        Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync("/inbox", Cancel)).StatusCode);
        Assert.Equal(["patients inbox"], await ReadsAsync(patient.Patient, clinician.Id, since));
    }

    // Writing a note records each of its reads as a note being written; the signed note's page as a visit note; and the
    // chart, which then lists it, records the read of the note it lists as part of the chart.
    [Fact]
    public async Task A_note_records_note_draft_while_written_visit_note_once_signed_and_chart_where_listed()
    {
        var (clinician, session, patient) = await PatientAsync();
        var since = DateTimeOffset.UtcNow;
        var note = await NoteAddendumTests.SignedNoteAsync(session.Client, patient.Patient);
        Assert.DoesNotContain(await ReadsAsync(patient.Patient, clinician.Id, since), read => !read.EndsWith(" note-draft", StringComparison.Ordinal)
            && read != "patients chart");
        Assert.Contains("clinical_drafts note-draft", await ReadsAsync(patient.Patient, clinician.Id, since));

        since = DateTimeOffset.UtcNow;
        Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync($"/patients/{patient.Patient}/notes/{note}", Cancel)).StatusCode);
        Assert.Equal(["clinical_notes visit-note", "patients visit-note"], await ReadsAsync(patient.Patient, clinician.Id, since));

        since = DateTimeOffset.UtcNow;
        Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync($"/patients/{patient.Patient}", Cancel)).StatusCode);
        Assert.Equal(["clinical_notes chart", "patients chart"], await ReadsAsync(patient.Patient, clinician.Id, since));
    }

    // The desktop app's offline copy of the clinician's panel: the chart and its notes, as the offline copy.
    [Fact]
    public async Task The_desktop_offline_copy_records_its_part()
    {
        var (clinician, session, patient) = await PatientAsync();
        await NoteAddendumTests.SignedNoteAsync(session.Client, patient.Patient);
        var token = await apps.DesktopSignInAsync(apps.OnPrem, clinician);
        var since = DateTimeOffset.UtcNow;

        using var offline = await DesktopSignInTests.PostAsync(apps.OnPrem, "/api/desktop/offline-panel", token);

        Assert.Equal(HttpStatusCode.OK, offline.StatusCode);
        Assert.Equal(["clinical_notes offline", "patients offline"], await ReadsAsync(patient.Patient, clinician.Id, since));
    }
}
