using System.Globalization;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VitalTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-23
// Purpose / scenario: With the clinic's time zone set, the vital signs page takes the time a set was taken on the
//   clinic's clock and shows times on it, labeled. What is stored is the same moment, in UTC, so a clinic set to UTC
//   shows the same reading at its UTC time. The administrator's audit log stays in UTC.
// Type: Functional (alternative flow: the clinic's time zone)
// Requirements: FR-9, FR-48 (proposed), proposed FR-52
// Use case: UC 4, Record clinical data
// Preconditions: The on-site app is configured with Ehr:Clinic:TimeZone America/Chicago and the abbreviations
//   "CST,CDT". The clinician is signed in with the patient on their panel.
// Inputs and system conditions: GET and POST /patients/{id}/vitals; GET /patients/{id}; GET /admin/audit.
// Test data: Heart rate 72, taken three hours ago, typed as Chicago's clock read then.
// Steps:
//   1. Open the vital signs page.
//   2. Record the heart rate with the time typed on the clinic's clock.
//   3. Open the page on an app whose clinic is in UTC.
//   4. As an administrator on the Chicago app, open the audit log for the patient.
// Expected result: Step 1: the field is labeled "Taken (clinic time, CST)" (CDT in summer) and holds the clinic's
//   time now. Step 2: saved; the flowsheet's column is headed with the time typed and the zone's abbreviation; the
//   reading is stored at that moment in UTC (the field's label is "Taken (clinic time)"); the link to mark the set in error still names the moment in UTC; the
//   chart shows the clinic's day. Step 3: the same reading is headed with its UTC time, labeled UTC. Step 4: the
//   audit log's times are headed "When (UTC)", the reading's event is at its UTC time, and none carries the clinic's
//   abbreviation. Also: a second reading makes a trend, whose table is in clinic time; a time the clocks skip is
//   refused with its own message; the desktop's offline copy names the clinic's zone.
// Creator: Tahseen Arnan
public class TC_UC4_23_VitalSignsInClinicTime(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Vital_signs_are_taken_and_shown_on_the_clinics_clock_and_stored_as_the_same_moment()
    {
        var clock = ClinicTimeTests.ChicagoClock;
        using var chicago = ClinicTimeTests.Chicago(apps);
        var (clinician, patient, utcSession) = await OnPanelAsync(apps);
        var session = await SignInAsync(chicago, clinician);
        var now = DateTimeOffset.UtcNow;

        var form = await session.Client.GetStringAsync(Path(patient), Cancel);
        Assert.Contains("Taken (clinic time) <span>(required)</span>", form);
        var offered = System.Text.RegularExpressions.Regex.Match(form, "id=\"vital-taken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var offeredAt = clock.From(DateTime.ParseExact(offered, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture))!.Value;
        Assert.InRange(offeredAt, now.AddMinutes(-2), now.AddMinutes(2));

        // Three hours ago by the database's clock, which judges the time, to the minute, typed as the clinic's clock
        // read then.
        var databaseNow = await apps.Database.NowAsync();
        var taken = new DateTimeOffset(databaseNow.Year, databaseNow.Month, databaseNow.Day, databaseNow.Hour, databaseNow.Minute, 0, TimeSpan.Zero).AddHours(-3);
        var typed = clock.Plain(taken, "yyyy-MM-ddTHH:mm");
        Assert.NotEqual(taken.UtcDateTime.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture), typed);
        using var recorded = await RecordAtAsync(session.Client, patient, typed, (HeartRate, "72"));
        var page = await ReadAsync(recorded);
        Assert.Contains("Saved.", page);
        Assert.Contains($"<th scope=\"col\" class=\"sc-table__mono\">{clock.Format(taken)}</th>", page);
        Assert.Contains("Times are clinic time.", page);
        Assert.Equal([taken.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)], await ProxyTests.QueryAsync(apps,
            "SELECT to_char(effective_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI') FROM ehr.observations WHERE patient_id = @patient AND category = 'vital-signs'", patient));
        // The set is still named by its moment in UTC, wherever the clinic is.
        var moment = taken.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        Assert.True(page.Contains($"at={moment}", StringComparison.Ordinal) || page.Contains($"at={moment.Replace(":", "%3A", StringComparison.Ordinal)}", StringComparison.Ordinal));
        var chart = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.Contains($"· {clock.Day(taken)}</span>", chart);

        // A second reading makes a trend: its sentence and its table are on the clinic's clock too.
        var earlier = taken.AddDays(-1);
        using var second = await RecordAtAsync(session.Client, patient, clock.Plain(earlier, "yyyy-MM-ddTHH:mm"), (HeartRate, "80"));
        var trends = await ReadAsync(second);
        Assert.Contains("<th scope=\"col\">Taken (clinic time)</th>", trends);
        Assert.Contains($"<td class=\"sc-table__mono\">{clock.Format(earlier)}</td>", trends);
        Assert.Contains($"2 values from {clock.Plain(earlier, "MMM d, yyyy")} to {clock.Plain(taken, "MMM d, yyyy")}.", Words(trends));

        // The hour Chicago's clocks skipped on 8 March 2026: said, not taken for a missing time.
        using var skipped = await RecordAtAsync(session.Client, patient, "2026-03-08T02:30", (HeartRate, "70"));
        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, skipped.StatusCode);
        Assert.Contains("The clocks go forward that day, so that time doesn&#x27;t exist. Enter another time.", await skipped.Content.ReadAsStringAsync(Cancel));

        // The desktop's offline copy says which zone its times are shown in.
        using var offline = await DesktopSignInTests.PostAsync(chicago, "/api/desktop/offline-panel", await apps.DesktopSignInAsync(chicago, clinician));
        Assert.Contains("\"clinicTimeZone\":\"America/Chicago\"", await offline.Content.ReadAsStringAsync(Cancel));

        // A clinic in UTC: the same reading, at the same moment, on its own clock.
        var utc = await utcSession.Client.GetStringAsync(Path(patient), Cancel);
        Assert.Contains($"<th scope=\"col\" class=\"sc-table__mono\">{taken.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC</th>", utc);
        Assert.Contains("Taken (UTC) <span>(required)</span>", utc);

        // The audit log stays in UTC.
        var admin = await SignInAsync(chicago, await apps.CreateStaffAsync("The Reviewer of Clocks", Ehr.Data.Accounts.StaffRoles.Admin));
        var log = await admin.Client.GetStringAsync($"/admin/audit?Patient={patient}", Cancel);
        Assert.Contains("<th>When (UTC)</th>", log);
        Assert.Contains("observations", log);
        // The reading's own event, at its UTC time: not moved to the clinic's clock with the label left off.
        var written = (await ProxyTests.QueryAsync(apps,
            "SELECT to_char(max(occurred_at) AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI') FROM ehr.audit_events WHERE patient_id = @patient AND table_name = 'observations' AND action = 'insert'", patient)).Single();
        Assert.Contains(written, log);
        Assert.DoesNotMatch("\\d{2}:\\d{2}(:\\d{2})? C[SD]T", log);
    }
}
