using System.Globalization;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The portal on the clinic's clock (Tahseen's decision of 26 November 2026, reversing step 10d's choice for the
// portal): with the clinic in America/Chicago, each kind of time a patient reads is the moment a clinician's page shows
// for the same row, on the same clock, with the zone's abbreviation for that moment. Rows are made through the pages on
// the app whose clinic is in UTC, as every other test's is, and read on the Chicago app: what is stored doesn't depend
// on the clinic's zone.
public class PortalClinicTimeTests(WebApps apps) : IClassFixture<WebApps>
{
    static readonly Ehr.Domain.Time.ClinicTime Clock = ClinicTimeTests.ChicagoClock;

    // A column's moments among the patient's rows of a table, earliest first, to the second, in UTC.
    static async Task<List<DateTimeOffset>> MomentsAsync(WebApps apps, string table, string column, Guid patient) =>
        [.. (await ProxyTests.QueryAsync(apps,
                $"SELECT to_char({column} AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS') FROM ehr.{table} WHERE patient_id = @patient ORDER BY {column}", patient))
            .Select(text => DateTimeOffset.ParseExact(text, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal))];

    static async Task<DateTimeOffset> MomentAsync(WebApps apps, string table, string column, Guid patient) => (await MomentsAsync(apps, table, column, patient))[^1];

    static Task<string> GetAsync(WebApplicationFactory<Program> app, System.Net.CookieContainer jar, string path) => Client(app, jar).GetStringAsync(path, Cancel);

    [Fact]
    public async Task An_appointment_in_winter_and_one_in_summer_read_as_the_clinics_appointment_page_has_them()
    {
        using var chicago = ClinicTimeTests.Chicago(apps);
        var (clinician, patient, _) = await AppointmentTests.ClinicianAsync(apps, "Dr. Clock Seasons");
        var staff = await SignInAsync(chicago, clinician);
        var year = DateTime.UtcNow.Year + 1;
        foreach (var day in new[] { $"{year}-01-12", $"{year}-07-13" })
        {
            using var booked = await AppointmentTests.BookAsync(staff.Client, patient, clinician.Id, day, "09:30", "Synthetic: seasons");
            Assert.Equal(System.Net.HttpStatusCode.OK, booked.StatusCode);
        }

        var (_, jar) = await PortalResultsTests.SignInPatientAsync(apps, chicago, patient);
        var home = await GetAsync(chicago, jar, "/portal");
        var list = await GetAsync(chicago, jar, "/portal/appointments");
        var print = await GetAsync(chicago, jar, "/portal/record/print");
        // Earliest first: the winter one, then the summer one.
        var ids = await AppointmentTests.RowsAsync(apps, patient, "id::text");
        var starts = await MomentsAsync(apps, "appointments", "starts_at", patient);
        Assert.Equal(2, ids.Count);
        foreach (var (id, start, label) in ids.Zip(starts, ["CST", "CDT"]))
        {
            Assert.Equal(label, Clock.Label(start));
            // The clinic's page for the appointment, and the portal's three pages that show it: the same time and label.
            var clinical = await staff.Client.GetStringAsync($"/appointments/{id}", Cancel);
            Assert.Contains($"{Clock.Plain(start, "dddd, MMMM d, yyyy")}, 09:30 to 09:50 {label}", clinical);
            Assert.All(new[] { home, list }, page =>
            {
                Assert.Contains($"<h3>{Clock.Plain(start, "dddd, MMM d, yyyy")}</h3>", page);
                Assert.Contains($"09:30 {label}, for 20 minutes, with Dr. Clock Seasons", page);
            });
            Assert.Contains($"09:30 {label}, 20 minutes", print);
        }

        Assert.All(new[] { home, list, print }, page => Assert.DoesNotContain(" UTC", page));
        Assert.Matches("as of [A-Z][a-z]{2} \\d{1,2}, \\d{4} \\d{2}:\\d{2} C[SD]T\\.", Words(print));
    }

    [Fact]
    public async Task A_result_and_vital_signs_are_shown_on_the_clinics_clock_and_calendar()
    {
        using var chicago = ClinicTimeTests.Chicago(apps);
        var (clinician, patient, session) = await OrderTests.OnPanelAsync(apps);
        // 03:00 on 16 November in UTC is the evening of the 15th in Chicago: the clinic's day is the one shown.
        var late = new DateTimeOffset(2026, 11, 16, 3, 0, 0, TimeSpan.Zero);
        await apps.InsertResultAsync(patient, "Hemoglobin A1c", "4548-4", 6.9m, "%", 4.0m, 5.6m, late, late);
        using var recorded = await VitalTests.RecordAsync(apps, session.Client, patient, 10, (VitalTests.HeartRate, "72"));
        var taken = await MomentAsync(apps, "observations", "effective_at", patient);

        var (_, jar) = await PortalResultsTests.SignInPatientAsync(apps, chicago, patient);
        var results = await GetAsync(chicago, jar, "/portal/results");
        Assert.Contains("<span class=\"sc-result__date\">Nov 15, 2026 CST</span>", results);
        Assert.DoesNotContain("Nov 16, 2026", results);
        Assert.Contains("value=\"About my Hemoglobin A1c result from Nov 15, 2026\"", results);

        var print = Words(await GetAsync(chicago, jar, "/portal/record/print"));
        Assert.Contains("Nov 15, 2026 CST", print);
        // The vital signs' time in the printable record is the clinician's flowsheet's, on the same clock.
        Assert.Contains(Clock.Format(taken, "MMM d, yyyy HH:mm"), print);
        var flowsheet = await (await SignInAsync(chicago, clinician)).Client.GetStringAsync(VitalTests.Path(patient), Cancel);
        Assert.Contains(Clock.Format(taken), flowsheet);
        Assert.DoesNotContain(" UTC", print);
    }

    [Fact]
    public async Task A_message_reads_at_the_same_time_in_the_portal_and_in_the_clinic()
    {
        using var chicago = ClinicTimeTests.Chicago(apps);
        var (clinician, patient, _, portal) = await MessageTests.CareAsync(apps, "Dr. Clock Messages");
        var thread = await MessageTests.StartAsync(portal.Client, "Synthetic: a question", "Synthetic: is this time right?");
        var sent = await MomentAsync(apps, "messages", "sent_at", patient);

        var shown = Clock.Format(sent, "MMM d, yyyy HH:mm");
        Assert.Contains(shown, await GetAsync(chicago, portal.Jar, $"/portal/messages/{thread}"));
        Assert.Contains($"Last message {Clock.Plain(sent, "MMM d, yyyy")}", await GetAsync(chicago, portal.Jar, "/portal/messages"));
        var clinical = await (await SignInAsync(chicago, clinician)).Client.GetStringAsync($"/patients/{patient}/messages/{thread}", Cancel);
        Assert.Contains(shown, clinical);
    }

    [Fact]
    public async Task A_pre_visit_form_names_its_appointment_at_the_time_the_clinics_page_does()
    {
        using var chicago = ClinicTimeTests.Chicago(apps);
        var visit = await PreVisitFormTests.VisitAsync(apps, "Dr. Clock Forms");
        using var asked = await PreVisitFormTests.AskAsync(visit.Session.Client, visit.Patient, visit.Appointment, PreVisitFormTests.Gad7);
        var start = await MomentAsync(apps, "appointments", "starts_at", visit.Patient);

        var (_, jar) = await PreVisitFormTests.PortalAsync(apps, visit);
        var forms = PreVisitFormTests.Text(await GetAsync(chicago, jar, "/portal/forms"));
        Assert.Contains($"Before your appointment on {Clock.Format(start, "MMM d, yyyy, HH:mm")} with Dr. Clock Forms.", forms);
        Assert.Contains($"Due {Clock.Format(start.AddMinutes(20), "MMM d, yyyy, HH:mm")}.", forms);
        var clinical = await (await SignInAsync(chicago, visit.Clinician)).Client.GetStringAsync($"/appointments/{visit.Appointment}", Cancel);
        Assert.Contains(Clock.Range(start, start.AddMinutes(20)), clinical);
    }

    [Fact]
    public async Task A_visit_summary_is_dated_as_its_signed_note_on_the_clinics_calendar()
    {
        using var chicago = ClinicTimeTests.Chicago(apps);
        var (clinician, session, patient) = await VisitSummaryTests.PortalPatientAsync(apps, "Clock");
        var note = await NoteAddendumTests.SignedNoteAsync(session.Client, patient.Patient);
        using var released = await VisitSummaryTests.ReleaseAsync(session.Client, patient.Patient, note);
        var signed = await MomentAsync(apps, "clinical_notes", "signed_at", patient.Patient);

        Assert.Contains($"<h2>{Clock.Format(signed, "MMM d, yyyy")}</h2>", await GetAsync(chicago, patient.Jar, "/portal/summaries"));
        var clinical = await (await SignInAsync(chicago, clinician)).Client.GetStringAsync(NoteAddendumTests.NotePath(patient.Patient, note), Cancel);
        Assert.Contains(Clock.Format(signed, "yyyy-MM-dd HH:mm:ss"), clinical);
    }

    [Fact]
    public async Task Who_viewed_my_record_lists_each_view_on_the_clinics_clock()
    {
        using var chicago = ClinicTimeTests.Chicago(apps);
        var (clinician, _, patient) = await RecordViewTests.ViewedPatientAsync(apps, "Clock Views");
        await RecordViewTests.ReadsAsync(apps, patient.Patient, clinician.Id, 1, newestMinutesAgo: 2);
        var viewed = await RecordViewTests.LatestAsync(apps, patient.Patient, clinician.Id);

        var page = await GetAsync(chicago, patient.Jar, RecordViewTests.ViewsPath);
        Assert.Contains(RecordViewTests.Lines(page), line => line.StartsWith("staff | ", StringComparison.Ordinal)
            && line.EndsWith(" | " + Clock.Format(viewed, "MMM d, yyyy, HH:mm"), StringComparison.Ordinal));
        Assert.DoesNotContain("Times are", page);
        Assert.DoesNotContain(" UTC", page);
    }

    [Fact]
    public async Task A_refill_request_is_dated_as_the_clinics_review_page_dates_it()
    {
        using var chicago = ClinicTimeTests.Chicago(apps);
        var (clinician, _, patient) = await VisitSummaryTests.PortalPatientAsync(apps, "Clock Refills");
        await RefillRequestTests.MedicationAsync(apps, patient.Patient);
        using var asked = await RefillRequestTests.AskAsync(patient.Client, RefillRequestTests.Lisinopril);
        var requested = await MomentAsync(apps, "refill_requests", "requested_at", patient.Patient);
        var request = await RefillRequestTests.RequestIdAsync(apps, patient.Patient);

        Assert.Contains($"Asked {Clock.Format(requested, "MMM d, yyyy")} by you", Words(await GetAsync(chicago, patient.Jar, RefillRequestTests.PortalPath)));
        var clinical = await (await SignInAsync(chicago, clinician)).Client.GetStringAsync(RefillRequestTests.ReviewPath(patient.Patient, request), Cancel);
        Assert.Contains(Clock.Format(requested), clinical);
    }
}
