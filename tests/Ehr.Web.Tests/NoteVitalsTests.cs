using System.Net;
using System.Text.Json;
using Ehr.Data.Accounts;
using Ehr.Data.Patients;
using Ehr.Domain.Time;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The vital signs a note's Objective is offered (the expansion plan's step 14), asked for as note-editor.js asks: every
// sign of the latest clinic day with one standing, however many values that day holds, and only for the clinician's own
// unsigned draft of the route's patient. TC-UC14-30 is the clinician's flow; ObjectiveVitalsTests works out the text.
public class NoteVitalsTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    ClinicTime Clinic => apps.OnPrem.Services.GetRequiredService<ClinicTime>();

    // A clinic day two days ago, by the database's clock (the tests' clinic clock is UTC): every value here is on it.
    async Task<DateTime> DayAsync() => (await apps.Database.NowAsync()).Date.AddDays(-2);

    // Vital signs as the superuser inserts them, each with the name and unit db/reference gives its code: one value, or
    // a blood pressure's two of one reading.
    async Task InsertAsync(Guid patient, DateTime at, params (string Code, decimal Value)[] values)
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ehr.observations (id, patient_id, reading_id, category, code, display, status, value, unit, effective_at, issued_at)
            SELECT gen_random_uuid(), @patient, CASE WHEN v.code IN ('8480-6', '8462-4') THEN @reading END, 'vital-signs', v.code, c.display, 'final', v.value,
                c.unit, @at, @at
            FROM unnest(@codes, @values) AS v(code, value) JOIN ehr.observation_codes c ON c.code = v.code
            """, connection);
        command.Parameters.AddWithValue("patient", patient);
        command.Parameters.AddWithValue("reading", Guid.CreateVersion7());
        command.Parameters.AddWithValue("at", DateTime.SpecifyKind(at, DateTimeKind.Utc));
        command.Parameters.AddWithValue("codes", values.Select(v => v.Code).ToArray());
        command.Parameters.AddWithValue("values", values.Select(v => v.Value).ToArray());
        await command.ExecuteNonQueryAsync(Cancel);
    }

    // `count` heart rates a minute apart from 07:01, the n-th 60 + n % 40 per minute: each newer than 07:00.
    async Task InsertHeartRatesAsync(Guid patient, DateTime day, int count)
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ehr.observations (id, patient_id, category, code, display, status, value, unit, effective_at, issued_at)
            SELECT gen_random_uuid(), @patient, 'vital-signs', c.code, c.display, 'final', 60 + n % 40, c.unit,
                @start + make_interval(mins => n), @start + make_interval(mins => n)
            FROM generate_series(1, @count) AS n JOIN ehr.observation_codes c ON c.code = '8867-4'
            """, connection);
        command.Parameters.AddWithValue("patient", patient);
        command.Parameters.AddWithValue("start", DateTime.SpecifyKind(day.AddHours(7), DateTimeKind.Utc));
        command.Parameters.AddWithValue("count", count);
        await command.ExecuteNonQueryAsync(Cancel);
    }

    static async Task<string> DraftAsync(HttpClient client, Guid patient)
    {
        using var created = await PostFormAsync(client, $"/patients/{patient}?handler=NewNote", $"/patients/{patient}");
        return PathOf(created);
    }

    static async Task<string?> OfferAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path + "?handler=Vitals", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.GetProperty("text").GetString();
    }

    string At(DateTime day, int hour, int minute) => Clinic.Format(new DateTimeOffset(day.AddHours(hour).AddMinutes(minute), TimeSpan.Zero), "HH:mm");

    // A day with more values than a page of them: a temperature taken early, then 125 heart rates. Each sign is offered.
    [Fact]
    public async Task Every_sign_taken_on_the_day_is_offered_however_many_values_the_day_holds()
    {
        var (_, patient, session) = await ChartListTests.OnPanelAsync(apps, "Measured");
        var day = await DayAsync();
        await InsertAsync(patient, day.AddHours(6), (VitalSign.Temperature, 39.1m));
        await InsertHeartRatesAsync(patient, day, 125);

        Assert.Equal(
            $"Vital signs, {day:yyyy-MM-dd}: heart rate 65 per minute at {At(day, 9, 5)}; temperature 39.1 °C at {At(day, 6, 0)}.",
            await OfferAsync(session.Client, await DraftAsync(session.Client, patient)));
    }

    // A blood pressure taken before 119 heart rates is the 120th and 121st newest value of the day: it is offered whole.
    [Fact]
    public async Task A_blood_pressure_is_offered_whole_wherever_it_falls_among_the_days_values()
    {
        var (_, patient, session) = await ChartListTests.OnPanelAsync(apps, "Pressured");
        var day = await DayAsync();
        await InsertAsync(patient, day.AddHours(6).AddMinutes(30), (VitalSign.Systolic, 128), (VitalSign.Diastolic, 82));
        await InsertHeartRatesAsync(patient, day, 119);

        Assert.Equal(
            $"Vital signs, {day:yyyy-MM-dd}: blood pressure 128/82 mmHg at {At(day, 6, 30)}; heart rate 99 per minute at {At(day, 8, 59)}.",
            await OfferAsync(session.Client, await DraftAsync(session.Client, patient)));
    }

    // The offer is for the clinician's own unsigned draft of the route's patient. A draft that doesn't exist, another
    // patient's, another clinician's of this patient, and one signed are not found, and nothing is read or recorded.
    [Fact]
    public async Task Vital_signs_are_offered_only_for_the_readers_own_unsigned_draft_of_that_patient()
    {
        var (clinician, patient, session) = await ChartListTests.OnPanelAsync(apps, "Drafted");
        await InsertAsync(patient, (await DayAsync()).AddHours(6), (VitalSign.Temperature, 37.2m));
        var other = await apps.InsertPatientAsync("Synthetic", "Elsewhere");
        await apps.AssignAsync(clinician, other);
        var colleague = await apps.CreateStaffAsync("Dr. Colleague", StaffRoles.Clinician);
        await apps.AssignAsync(colleague, patient);
        var colleagues = await SignInAsync(apps.OnPrem, colleague);

        var own = await DraftAsync(session.Client, patient);
        var otherPatients = await DraftAsync(session.Client, other);
        var colleaguesDraft = await DraftAsync(colleagues.Client, patient);
        var note = await NoteAddendumTests.SignedNoteAsync(session.Client, patient);
        var signed = (await ProxyTests.QueryAsync(apps, $"SELECT draft_id::text FROM ehr.clinical_notes WHERE id = '{note}'", patient)).Single();
        string Id(string path) => path.Split('/')[^1];

        var since = await apps.Database.NowAsync();
        foreach (var draft in new[] { Guid.NewGuid().ToString(), Id(otherPatients), Id(colleaguesDraft), signed })
        {
            using var refused = await session.Client.GetAsync($"/patients/{patient}/notes/drafts/{draft}?handler=Vitals", Cancel);
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        }

        var recorded = $"SELECT count(*)::text FROM ehr.audit_events WHERE actor_id = '{clinician.Id}' AND occurred_at >= '{since:O}'";
        Assert.Equal(["0"], await ProxyTests.QueryAsync(apps, recorded, patient));

        Assert.Equal($"Vital signs, {Clinic.Format(new DateTimeOffset((await DayAsync()).AddHours(6), TimeSpan.Zero))}: temperature 37.2 °C.", await OfferAsync(session.Client, own));
        Assert.Equal(["1"], await ProxyTests.QueryAsync(apps, recorded + " AND patient_id = @patient AND part = 'vitals'", patient));
    }
}
