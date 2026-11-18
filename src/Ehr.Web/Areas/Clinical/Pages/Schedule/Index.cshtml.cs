using System.Globalization;
using System.Security.Claims;
using Ehr.Domain.Phi;
using Ehr.Domain.Scheduling;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Schedule;

// A clinician's day (FR-17): the appointments that start on one day, UTC, earliest first, for the signed-in clinician
// or a colleague. Read-only, so it works off-site like the chart. Each patient named is a recorded read of that
// patient (PhiReader.ScheduleAsync). For clinicians: the page's policy (Program.cs).
public class IndexModel(PhiReader phi, Appointments appointments, Ehr.Domain.Patients.PatientAssignments assignments) : PageModel
{
    // The clinician whose day it is; the signed-in one when not given.
    [BindProperty(SupportsGet = true)]
    public Guid? Clinician { get; set; }

    // The day, as year-month-day; today when not given or not a day.
    [BindProperty(SupportsGet = true)]
    public string? Day { get; set; }

    public DateOnly Date { get; private set; }

    public Guid Shown { get; private set; }

    public IReadOnlyList<(Guid Id, string Name)> Clinicians { get; private set; } = [];

    public IReadOnlyList<AppointmentEntry> Appointments { get; private set; } = [];

    // The reason for a visit is clinical: it is shown on the signed-in clinician's own day and for patients on their
    // panel, and left out of a colleague's day otherwise, as a chart off the panel would ask for a reason first.
    public HashSet<Guid> Panel { get; private set; } = [];

    public bool ShowsReason(AppointmentEntry appointment) => Shown == Me || Panel.Contains(appointment.PatientId);

    Guid Me { get; set; }

    public string Link(int days) => Url.Page("Index", new { clinician = Shown, day = Date.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) })!;

    public async Task OnGetAsync()
    {
        Response.Headers.CacheControl = "no-store";
        Date = DateOnly.TryParseExact(Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : DateOnly.FromDateTime(DateTime.UtcNow);
        Clinicians = await appointments.CliniciansAsync(HttpContext.RequestAborted);
        Me = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        Shown = Clinician is { } asked && Clinicians.Any(c => c.Id == asked) ? asked : Me;
        Panel = Shown == Me ? [] : await assignments.PanelIdsAsync(HttpContext.RequestAborted);
        Appointments = await phi.ScheduleAsync(Shown, Date, HttpContext.RequestAborted);
    }
}
