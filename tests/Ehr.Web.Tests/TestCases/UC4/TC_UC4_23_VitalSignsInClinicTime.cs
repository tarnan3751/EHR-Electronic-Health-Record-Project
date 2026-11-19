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
//   reading is stored at that moment in UTC; the link to mark the set in error still names the moment in UTC; the
//   chart shows the clinic's day. Step 3: the same reading is headed with its UTC time, labeled UTC. Step 4: the
//   audit log's times are headed "When (UTC)" and none carries the clinic's abbreviation.
// Creator: Tahseen Arnan
public class TC_UC4_23_VitalSignsInClinicTime(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Vital_signs_are_taken_and_shown_on_the_clinics_clock_and_stored_as_the_same_moment()
    {
        var clock = ClinicTimeTests.ChicagoClock;
        using var chicago = ClinicTimeTests.Chicago(apps);
        var (clinician, patient, utcSession) = await OnPanelAsync(apps);
        var session = await SignInAsync(chicago, clinician);
        var now = DateTimeOffset.UtcNow;

        var form = await session.Client.GetStringAsync(Path(patient), Cancel);
        Assert.Contains($"Taken ({clock.FieldLabel(now)}) <span>(required)</span>", form);
        var offered = System.Text.RegularExpressions.Regex.Match(form, "id=\"vital-taken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var offeredAt = clock.From(DateTime.ParseExact(offered, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture))!.Value;
        Assert.InRange(offeredAt, now.AddMinutes(-2), now.AddMinutes(2));

        // Three hours ago, to the minute, typed as the clinic's clock read then.
        var taken = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero).AddHours(-3);
        var typed = clock.Plain(taken, "yyyy-MM-ddTHH:mm");
        Assert.NotEqual(taken.UtcDateTime.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture), typed);
        using var recorded = await RecordAtAsync(session.Client, patient, typed, (HeartRate, "72"));
        var page = await ReadAsync(recorded);
        Assert.Contains("Saved:", page);
        Assert.Contains($"<th scope=\"col\" class=\"sc-table__mono\">{clock.Format(taken)}</th>", page);
        Assert.Contains("Times are clinic time.", page);
        Assert.Equal([taken.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)], await ProxyTests.QueryAsync(apps,
            "SELECT to_char(effective_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI') FROM ehr.observations WHERE patient_id = @patient AND category = 'vital-signs'", patient));
        // The set is still named by its moment in UTC, wherever the clinic is.
        var moment = taken.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        Assert.True(page.Contains($"at={moment}", StringComparison.Ordinal) || page.Contains($"at={moment.Replace(":", "%3A", StringComparison.Ordinal)}", StringComparison.Ordinal));
        var chart = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.Contains($"· {clock.Day(taken)}</span>", chart);

        // A clinic in UTC: the same reading, at the same moment, on its own clock.
        var utc = await utcSession.Client.GetStringAsync(Path(patient), Cancel);
        Assert.Contains($"<th scope=\"col\" class=\"sc-table__mono\">{taken.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC</th>", utc);
        Assert.Contains("Taken (UTC) <span>(required)</span>", utc);

        // The audit log stays in UTC.
        var admin = await SignInAsync(chicago, await apps.CreateStaffAsync("The Reviewer of Clocks", Ehr.Data.Accounts.StaffRoles.Admin));
        var log = await admin.Client.GetStringAsync($"/admin/audit?Patient={patient}", Cancel);
        Assert.Contains("<th>When (UTC)</th>", log);
        Assert.Contains("observations", log);
        Assert.DoesNotMatch("\\d{2}:\\d{2}(:\\d{2})? C[SD]T", log);
    }
}
