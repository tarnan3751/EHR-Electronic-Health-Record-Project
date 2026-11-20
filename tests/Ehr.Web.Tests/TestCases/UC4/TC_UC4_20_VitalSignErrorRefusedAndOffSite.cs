using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VitalTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-20
// Purpose / scenario: Marking a reading in error needs a reading and a reason; a clinician without the patient on
//   their panel can't; off-site it is forwarded, and while the clinic's server can't be reached it isn't offered.
// Type: Functional (invalid input; exceptional condition)
// Requirements: FR-08, FR-02, FR-14, FR-15, proposed FR-50
// Use case: UC 4, View patient chart
// Preconditions: The clinician is signed in with the patient on their panel, who has one set of vital signs (heart
//   rate 72 and temperature 36.8, an hour ago). A covering clinician has opened the chart with a reason. The off-site
//   app can reach the clinic's server; a second off-site app can't.
// Inputs and system conditions: GET and POST /patients/{id}/vitals/error?at=... on each app.
// Test data: No reading ticked; no reason; reason "Wrong patient."
// Steps:
//   1. Post with a reason and nothing ticked, then with the heart rate ticked and no reason.
//   2. As the covering clinician, open the flowsheet, open the page, and post to it.
//   3. Off-site, mark the heart rate with the reason.
//   4. On the app that is cut off, open the flowsheet and the page, and post to it.
// Expected result: Step 1: each is answered 422 with "Not marked:" and "Choose at least one reading." or "Say why, so
//   the record shows it."; nothing changes. Step 2: the flowsheet has no "Mark in error" link; the page and the post
//   send them back to the chart; nothing changes. Step 3: forwarded, and the heart rate is entered in error. Step 4:
//   the flowsheet has no link, the page says "Readings can't be marked until the clinic's server is back." with no
//   form, and the post is answered 503; the temperature still stands.
// Creator: Tahseen Arnan
public class TC_UC4_20_VitalSignErrorRefusedAndOffSite(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Marking_in_error_needs_a_reading_a_reason_and_the_panel_and_follows_the_off_site_rules()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        var at = DateTime.UtcNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm");
        using var recorded = await RecordAtAsync(session.Client, patient, at, (HeartRate, "72"), (Temperature, "36.8"));
        var set = at + ":00";
        string[] standing = [$"{Temperature} 36.8 final", $"{HeartRate} 72 final"];

        using var none = await MarkAsync(apps, session.Client, patient, set, "Wrong patient.");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode);
        Assert.Contains("Not marked: Choose at least one reading.", Words(await none.Content.ReadAsStringAsync(Cancel)));
        using var unexplained = await MarkAsync(apps, session.Client, patient, set, null, HeartRate);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unexplained.StatusCode);
        Assert.Contains("Say why, so the record shows it.", Words(await unexplained.Content.ReadAsStringAsync(Cancel)));
        Assert.Equal(standing, await StatesAsync(apps, patient));

        var covering = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Dr. Covering Vitals", Ehr.Data.Accounts.StaffRoles.Clinician));
        using var opened = await PostFormAsync(covering.Client, $"/patients/{patient}/reason", $"/patients/{patient}/reason", ("Reason", "Covering the night shift"));
        var token = Token(await ReadAsync(opened));
        var flowsheet = await covering.Client.GetStringAsync(Path(patient), Cancel);
        Assert.Contains("72", flowsheet);
        Assert.DoesNotContain("data-mark-in-error", flowsheet);
        var noRedirects = Client(apps.OnPrem, covering.Jar, followRedirects: false);
        using var page = await noRedirects.GetAsync(ErrorPath(patient, set), Cancel);
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal($"/patients/{patient}", page.Headers.Location?.OriginalString);
        using var posted = await noRedirects.PostAsync(ErrorPath(patient, set), Form(("__RequestVerificationToken", token), ("Reason", "Wrong patient.")), Cancel);
        Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);
        Assert.Equal(standing, await StatesAsync(apps, patient));

        var cloud = Client(apps.Cloud, session.Jar);
        using var marked = await MarkAsync(apps, cloud, patient, set, "Wrong patient.", HeartRate);
        Assert.Contains(apps.Forwarded, forwarded => forwarded.StartsWith($"POST /patients/{patient}/vitals/error", StringComparison.Ordinal));
        Assert.Equal([$"{Temperature} 36.8 final", $"{HeartRate} 72 entered-in-error"], await StatesAsync(apps, patient));
        var own = Token(await session.Client.GetStringAsync(ErrorPath(patient, set), Cancel));

        // The cut-off app learns the clinic's server is down from its audit shipper, every couple of seconds: make it
        // look now, so the page is read knowing it, however soon after start-up this runs.
        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<Ehr.Web.CloudAudit.CloudAuditShipper>().ShipAsync(Cancel));
        var cutOff = Client(apps.CloudCutOff, session.Jar);
        Assert.DoesNotContain("data-mark-in-error", await cutOff.GetStringAsync(Path(patient), Cancel));
        var readOnly = await cutOff.GetStringAsync(ErrorPath(patient, set), Cancel);
        Assert.Contains("Readings can't be marked until the clinic's server is back.", readOnly);
        Assert.DoesNotContain("data-mark-entered-in-error", readOnly);
        using var notSaved = await cutOff.PostAsync(ErrorPath(patient, set), Form(("__RequestVerificationToken", own), ("Reason", "During the outage.")), Cancel);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notSaved.StatusCode);
        Assert.Equal([$"{Temperature} 36.8 final", $"{HeartRate} 72 entered-in-error"], await StatesAsync(apps, patient));
    }
}
