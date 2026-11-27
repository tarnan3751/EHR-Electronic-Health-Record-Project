using System.Globalization;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VitalTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-24
// Purpose / scenario: With the clinic's time zone set, the portal's times are the clinic's, as the clinician's are
//   (Tahseen's decision of 26 November 2026; until then the portal stayed in UTC). The portal and the clinician pages
//   share the partial that draws a trend, which shows both the clinic's clock, labeled.
// Type: Functional (alternative flow: the clinic's time zone)
// Requirements: FR-48 (proposed), proposed FR-52
// Use case: UC 4, Record clinical data (the portal page is UC 6's)
// Preconditions: The on-site app is configured with Ehr:Clinic:TimeZone America/Chicago. The clinician has recorded
//   two heart rates for the patient, who has a portal account.
// Inputs and system conditions: GET /portal/results as the patient; GET /patients/{id}/vitals as the clinician.
// Test data: Heart rates 72 and 64, a day apart.
// Steps:
//   1. As the patient, open the portal's results on the Chicago app.
//   2. As the clinician, open the vital signs page on the Chicago app.
// Expected result: Step 1: the portal page heads the trend's table "Taken (clinic time)" and lists the readings on
//   Chicago's clock, labeled, with no "UTC" anywhere on the page. Step 2: the clinician's table is headed "Taken (clinic
//   time)" and lists the same readings, the same way.
// Creator: Tahseen Arnan
public class TC_UC4_24_PortalTimesOnTheClinicsClock(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task The_portal_shows_the_clinics_clock_when_the_clinic_has_a_time_zone()
    {
        var clock = ClinicTimeTests.ChicagoClock;
        using var chicago = ClinicTimeTests.Chicago(apps);
        var (clinician, patient, session) = await OnPanelAsync(apps);
        using var first = await RecordAsync(apps, session.Client, patient, 24 * 60 + 5, (HeartRate, "72"));
        using var second = await RecordAsync(apps, session.Client, patient, 10, (HeartRate, "64"));
        var times = (await ProxyTests.QueryAsync(apps,
            "SELECT to_char(effective_at AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI') FROM ehr.observations WHERE patient_id = @patient AND category = 'vital-signs' ORDER BY effective_at", patient))
            .Select(text => new DateTimeOffset(DateTime.ParseExact(text, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture), TimeSpan.Zero)).ToList();
        Assert.Equal(2, times.Count);

        {
            var portal = await PortalResultsTests.SignInPatientAsync(apps, chicago, patient);
            var page = await portal.Client.GetStringAsync("/portal/results", Cancel);
            Assert.Contains("<th scope=\"col\">Taken (clinic time)</th>", page);
            Assert.All(times, time => Assert.Contains($"<td class=\"sc-table__mono\">{clock.Format(time)}</td>", page));
            Assert.DoesNotContain("Taken (UTC)", page);
            Assert.DoesNotMatch("\\bUTC\\b", page);
        }

        var clinical = await (await SignInAsync(chicago, clinician)).Client.GetStringAsync(Path(patient), Cancel);
        Assert.Contains("<th scope=\"col\">Taken (clinic time)</th>", clinical);
        Assert.All(times, time => Assert.Contains($"<td class=\"sc-table__mono\">{clock.Format(time)}</td>", clinical));
    }
}
