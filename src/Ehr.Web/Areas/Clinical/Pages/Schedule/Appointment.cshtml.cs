using System.Globalization;
using Ehr.Data.Scheduling;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Ehr.Domain.Scheduling;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Schedule;

// One appointment (FR-17): who, when and why, and, while it is still booked and the patient is on the clinician's
// panel, the four things that can happen to it: reschedule, cancel (with a reason), check the patient in, which starts
// their visit, or mark a no-show. Open to whoever the chart is open to; the read is one recorded read of the patient.
// The database holds each change to those moves and refuses anything else (db/policies/80-appointments.sql). An
// appointment that someone else changed first isn't changed again: the page says so.
[ShownIfNotSaved(nameof(Reason), nameof(CancelReason))]
public class AppointmentModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass, Appointments appointments) : PageModel
{
    [BindProperty]
    public string? Day { get; set; }

    [BindProperty]
    public string? Time { get; set; }

    [BindProperty]
    public int Minutes { get; set; }

    [BindProperty]
    public string? Reason { get; set; }

    [BindProperty]
    public string? CancelReason { get; set; }

    public AppointmentDetail Detail { get; private set; } = null!;

    public bool OnPanel { get; private set; }

    public bool CanChange => OnPanel && Detail.Appointment.Status == AppointmentStatus.Booked;

    public IReadOnlyDictionary<string, string> Errors { get; private set; } = new Dictionary<string, string>();

    // What just happened, for the notice at the top.
    [BindProperty(SupportsGet = true)]
    public string? Saved { get; set; }

    // Set when the appointment was changed by someone else first.
    public bool Gone { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid appointmentId)
    {
        if (await LoadAsync(appointmentId) is { } refused)
        {
            return refused;
        }

        Fill();
        return Page();
    }

    public async Task<IActionResult> OnPostRescheduleAsync(Guid appointmentId)
    {
        if (await LoadAsync(appointmentId, changing: true) is { } refused)
        {
            return refused;
        }

        var errors = new Dictionary<string, string>();
        var start = AppointmentForm.Check(Day, Time, Minutes, Reason, errors);
        if (errors.Count == 0)
        {
            var result = await appointments.RescheduleAsync(appointmentId, start!.Value, Minutes, Reason!, HttpContext.RequestAborted);
            if (result == AppointmentResult.Overlap)
            {
                errors["Time"] = "This clinician already has an appointment booked then. Choose another time.";
                Response.StatusCode = StatusCodes.Status409Conflict;
            }
            else
            {
                return Done(result, appointmentId, "rescheduled");
            }
        }
        else
        {
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        }

        Errors = errors;
        return Page();
    }

    public async Task<IActionResult> OnPostCancelAsync(Guid appointmentId)
    {
        if (await LoadAsync(appointmentId, changing: true) is { } refused)
        {
            return refused;
        }

        var reason = CancelReason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > Appointments.MaximumReasonLength)
        {
            Errors = new Dictionary<string, string>
            {
                ["CancelReason"] = string.IsNullOrEmpty(reason)
                    ? "Enter the reason for cancelling."
                    : $"Shorten the reason to {Appointments.MaximumReasonLength} characters or fewer.",
            };
            Fill();
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            return Page();
        }

        return Done(await appointments.CancelAsync(appointmentId, reason, HttpContext.RequestAborted), appointmentId, "cancelled");
    }

    public async Task<IActionResult> OnPostCheckInAsync(Guid appointmentId) =>
        await LoadAsync(appointmentId, changing: true) ?? Done(
            await appointments.CheckInAsync(appointmentId, Detail.Patient.Id, Detail.Appointment.Reason, HttpContext.RequestAborted), appointmentId, "checked-in");

    public async Task<IActionResult> OnPostNoShowAsync(Guid appointmentId) =>
        await LoadAsync(appointmentId, changing: true) ?? Done(await appointments.NoShowAsync(appointmentId, HttpContext.RequestAborted), appointmentId, "no-show");

    IActionResult Done(AppointmentResult result, Guid appointmentId, string saved)
    {
        if (result == AppointmentResult.Saved)
        {
            return RedirectToPage(new { appointmentId, saved });
        }

        Gone = true;
        Fill();
        Response.StatusCode = StatusCodes.Status409Conflict;
        return Page();
    }

    // The reschedule form opens with the appointment as it is, unless the clinician is fixing what they entered.
    void Fill()
    {
        if (Errors.ContainsKey("Day") || Errors.ContainsKey("Time") || Errors.ContainsKey("Reason") || Errors.ContainsKey("Minutes"))
        {
            return;
        }

        var appointment = Detail.Appointment;
        Day = appointment.StartsAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Time = appointment.StartsAt.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        Minutes = appointment.Minutes;
        Reason = appointment.Reason;
    }

    // The gate, then the one recorded read. Null when the page may be shown. A change needs the patient on the panel.
    async Task<IActionResult?> LoadAsync(Guid appointmentId, bool changing = false)
    {
        Response.Headers.CacheControl = "no-store";
        // Whose appointment it is decides who may open it, and reading that is itself a read of the patient: so the
        // appointment is read first, recorded, and shown only to someone the chart is open to.
        if (await phi.AppointmentAsync(appointmentId, HttpContext.RequestAborted) is not { } detail)
        {
            return NotFound();
        }

        OnPanel = await assignments.IsOnPanelAsync(detail.Patient.Id, HttpContext.RequestAborted);
        if (!OnPanel && (changing || await breakGlass.ActiveUntilAsync(detail.Patient.Id, HttpContext.RequestAborted) is null))
        {
            return RedirectToPage("/Patients/Chart", new { id = detail.Patient.Id });
        }

        Detail = detail;
        return null;
    }
}
