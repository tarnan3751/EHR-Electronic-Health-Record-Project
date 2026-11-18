using System.Net;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.AppointmentTests;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC16;

// Test Case ID: TC-UC16-04
// Purpose / scenario: Three things that stop a change to an appointment. A clinician without the patient on their
//   panel can read it under emergency access and change nothing. An appointment someone else cancelled first isn't
//   changed again. And off-site with the clinic's server unreachable, the schedule is readable, booking and changing
//   are off the pages with a sentence saying why, and a post already on screen isn't saved.
// Type: Functional (exceptional condition)
// Requirements: FR-17, FR-33, FR-34, proposed FR-42
// Use case: UC 16, Schedule an appointment
// Preconditions: The first clinician has the patient on their panel and one appointment booked. A second clinician,
//   "Dr. Schedule Covering", doesn't. For the last part, the cloud app's link to the on-site server is down.
// Inputs and system conditions: GET and POST on /appointments/{id} and /patients/{id}/appointments/new, on the
//   on-site app as each clinician, then on the cloud app whose link is down.
// Test data: An appointment 34 days ahead at 13:00, reason "Synthetic: follow-up".
// Steps:
//   1. As the second clinician, request the appointment and the booking form; open the chart with a reason; open the
//      appointment; post a cancellation.
//   2. As the first clinician, cancel the appointment; then post "Mark as a no-show" for it.
//   3. Book another appointment. Off-site with the link down, open the chart, the day and the appointment, and post
//      a cancellation.
// Expected result: Step 1: before giving a reason, both requests are answered with a redirect to the chart; after,
//   the appointment page shows the appointment and "Add this patient to your panel to change their appointments.",
//   with no forms, and the post changes nothing. Step 2: the second post is answered 409 with "Not saved: this
//   appointment was changed by someone else first." Step 3: the chart says "Booking an appointment needs the clinic's
//   server."; the day lists the appointment; its page says "Changing an appointment needs the clinic's server. The
//   schedule remains readable."; the post is answered 503 and the appointment stays booked.
// Creator: Tahseen Arnan
public class TC_UC16_04_ExceptionalCondition(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Off_the_panel_already_changed_or_off_site_with_the_server_down_an_appointment_is_not_changed()
    {
        var (clinician, patient, session) = await ClinicianAsync(apps, "Dr. Schedule Four");
        using var booked = await BookAsync(session.Client, patient, clinician.Id, Day(4), "13:00", "Synthetic: follow-up");
        var appointment = await IdAsync(apps, patient);

        var covering = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Dr. Schedule Covering", StaffRoles.Clinician));
        var noRedirects = Client(apps.OnPrem, covering.Jar, followRedirects: false);
        foreach (var path in new[] { $"/appointments/{appointment}", NewPath(patient) })
        {
            using var sent = await noRedirects.GetAsync(path, Cancel);
            Assert.Equal($"/patients/{patient}", new Uri(Base, sent.Headers.Location!).AbsolutePath);
        }

        using var opened = await PostFormAsync(covering.Client, $"/patients/{patient}/reason", $"/patients/{patient}/reason", ("Reason", "Covering the night shift"));
        var chart = await ReadAsync(opened);
        Assert.DoesNotContain("data-book-appointment", chart);
        var readOnly = await covering.Client.GetStringAsync($"/appointments/{appointment}", Cancel);
        Assert.Contains("Synthetic: follow-up", readOnly);
        Assert.Contains("Add this patient to your panel to change their appointments.", readOnly);
        Assert.DoesNotContain("<form method=\"post\" action=\"/appointments", readOnly);
        using var refused = await noRedirects.PostAsync($"/appointments/{appointment}?handler=Cancel",
            Form(("__RequestVerificationToken", Token(readOnly)), ("CancelReason", "Synthetic")), Cancel);
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.Equal(["booked"], await RowsAsync(apps, patient));

        var stale = Token(await session.Client.GetStringAsync($"/appointments/{appointment}", Cancel));
        using var cancelled = await PostFormAsync(session.Client, $"/appointments/{appointment}?handler=Cancel", $"/appointments/{appointment}", ("CancelReason", "Synthetic: first"));
        using var late = await session.Client.PostAsync($"/appointments/{appointment}?handler=NoShow", Form(("__RequestVerificationToken", stale)), Cancel);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Contains("Not saved: this appointment was changed by someone else first.", Words(await late.Content.ReadAsStringAsync(Cancel)));
        Assert.Equal(["cancelled"], await RowsAsync(apps, patient));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Update, clinician.Id, patient));

        using var again = await BookAsync(session.Client, patient, clinician.Id, Day(4), "15:00", "Synthetic: rebooked");
        var second = Guid.Parse((await RowsAsync(apps, patient, "id::text")).Last());
        var token = Token(await session.Client.GetStringAsync($"/appointments/{second}", Cancel));
        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));
        var cutOff = Client(apps.CloudCutOff, session.Jar);
        Assert.Contains("Booking an appointment needs the clinic's server.", await cutOff.GetStringAsync($"/patients/{patient}", Cancel));
        Assert.Contains("15:00 to 15:20 UTC", await cutOff.GetStringAsync($"/schedule?day={Day(4)}", Cancel));
        var page = await cutOff.GetStringAsync($"/appointments/{second}", Cancel);
        Assert.Contains("Changing an appointment needs the clinic's server. The schedule remains readable.", page);
        Assert.DoesNotContain("Cancel appointment</button>", page);
        using var notSaved = await cutOff.PostAsync($"/appointments/{second}?handler=Cancel",
            Form(("__RequestVerificationToken", token), ("CancelReason", "Synthetic: typed off-site")), Cancel);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notSaved.StatusCode);
        Assert.Equal(["cancelled", "booked"], await RowsAsync(apps, patient));
    }
}
