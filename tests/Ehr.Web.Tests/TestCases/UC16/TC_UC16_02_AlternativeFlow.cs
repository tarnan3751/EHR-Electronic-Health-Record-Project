using Ehr.Data.Accounts;
using Xunit;
using static Ehr.Web.Tests.AppointmentTests;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC16;

// Test Case ID: TC-UC16-02
// Purpose / scenario: An appointment is rescheduled, booked with a colleague, cancelled with a reason, and marked as a
//   no-show. Nothing is deleted: each keeps its record, and the colleague's day shows theirs.
// Type: Functional (alternative flow)
// Requirements: FR-17, proposed FR-42
// Use case: UC 16, Schedule an appointment
// Preconditions: Two clinicians; the first is signed in on the on-site app with the patient on their panel.
// Inputs and system conditions: POST /patients/{id}/appointments/new; POST /appointments/{id} with the handlers
//   Reschedule, Cancel and NoShow; GET /schedule?clinician=...&day=...; on the on-site app.
// Test data: Clinicians "Dr. Schedule Two" and "Dr. Schedule Colleague". Three appointments on a day 32 days ahead:
//   10:00 with the first clinician, 11:00 with the colleague, 14:00 with the first. Cancellation reason "Synthetic:
//   patient asked to cancel".
// Steps:
//   1. Book the three appointments.
//   2. Reschedule the first to 10:30, 30 minutes, with a new reason.
//   3. Cancel the second, with the reason.
//   4. Mark the third as a no-show.
//   5. Open the colleague's day, and the first clinician's.
// Expected result: The first appointment is booked at 10:30 for 30 minutes with the new reason. The second is
//   cancelled with its reason and still exists. The third is a no-show. The colleague's day lists the cancelled
//   appointment with "Cancelled"; the first clinician's day lists 10:30 to 11:00 "Booked" and 14:00 "No-show". The
//   chart's upcoming list shows only the booked one.
// Creator: Tahseen Arnan
public class TC_UC16_02_AlternativeFlow(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_appointment_is_rescheduled_cancelled_or_marked_a_no_show_and_none_is_deleted()
    {
        var (clinician, patient, session) = await ClinicianAsync(apps, "Dr. Schedule Two");
        var colleague = await apps.CreateStaffAsync("Dr. Schedule Colleague", StaffRoles.Clinician);
        using var a = await BookAsync(session.Client, patient, clinician.Id, Day(2), "10:00", "Synthetic: first");
        using var b = await BookAsync(session.Client, patient, colleague.Id, Day(2), "11:00", "Synthetic: second");
        using var c = await BookAsync(session.Client, patient, clinician.Id, Day(2), "14:00", "Synthetic: third");
        var ids = (await RowsAsync(apps, patient, "id::text")).Select(Guid.Parse).ToList();
        Assert.Equal(3, ids.Count);

        using var moved = await ChangeAsync(session.Client, ids[0], "Reschedule",
            ("Day", Day(2)), ("Time", "10:30"), ("Minutes", 30), ("Reason", "Synthetic: first, moved"));
        Assert.Contains("Appointment rescheduled:", Words(await ReadAsync(moved)));
        using var cancelled = await ChangeAsync(session.Client, ids[1], "Cancel",
            ("CancelReason", "Synthetic: patient asked to cancel"));
        var cancelledPage = await ReadAsync(cancelled);
        Assert.Contains("Appointment cancelled:", Words(cancelledPage));
        Assert.Contains("Synthetic: patient asked to cancel", cancelledPage);
        using var missed = await ChangeAsync(session.Client, ids[2], "NoShow");
        Assert.Contains("Marked as a no-show:", Words(await ReadAsync(missed)));

        Assert.Equal(["booked 30 Synthetic: first, moved -", "cancelled 20 Synthetic: second Synthetic: patient asked to cancel", "noshow 20 Synthetic: third -"],
            await RowsAsync(apps, patient, "concat_ws(' ', status, minutes, reason, coalesce(cancel_reason, '-'))"));

        var theirs = await session.Client.GetStringAsync($"/schedule?clinician={colleague.Id}&day={Day(2)}", Cancel);
        Assert.Contains("11:00 to 11:20 UTC", theirs);
        Assert.Contains(">Cancelled</span>", theirs);
        Assert.DoesNotContain("10:30 to", theirs);
        var mine = await session.Client.GetStringAsync($"/schedule?day={Day(2)}", Cancel);
        Assert.Contains("10:30 to 11:00 UTC", mine);
        Assert.Contains(">No-show</span>", mine);
        // The colleague, without the patient on their panel, sees the first clinician's day without the reasons.
        var colleagueSession = await SignInAsync(apps.OnPrem, colleague);
        var seen = await colleagueSession.Client.GetStringAsync($"/schedule?clinician={clinician.Id}&day={Day(2)}", Cancel);
        Assert.Contains("10:30 to 11:00 UTC", seen);
        Assert.Contains("Not on your panel", seen);
        Assert.DoesNotContain("Synthetic: first, moved", seen);
        // On their own day they see the reason for their own appointment.
        Assert.Contains("Synthetic: second", await colleagueSession.Client.GetStringAsync($"/schedule?day={Day(2)}", Cancel));
        var chart = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.Contains("Synthetic: first, moved", chart);
        Assert.DoesNotContain("Synthetic: second", chart);
        Assert.DoesNotContain("Synthetic: third", chart);
    }
}
