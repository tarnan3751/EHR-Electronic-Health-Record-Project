using System.Globalization;
using System.Security.Claims;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Ehr.Domain.Scheduling;
using Ehr.Web.Areas.Clinical.Pages.Patients;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Schedule;

// Books an appointment for a patient (FR-17): with which clinician, when (the clinic's clock), for how long and why. The order
// form's rule: only for a patient on the clinician's panel; anyone else is sent back to the chart before anything is
// read. The database refuses a time the clinician is already booked for, even when two bookings arrive at once, and
// the form then says so with what was entered still in it. Off-site the post is forwarded; when it can't be, the
// cloud app's "Not saved" page shows the reason typed again (UC 2, ShownIfNotSaved).
[ShownIfNotSaved(nameof(Reason))]
public class NewModel(PhiReader phi, PatientAssignments assignments, Appointments appointments, Ehr.Domain.Time.ClinicTime clinic) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public SearchQuery Search { get; set; } = SearchQuery.None;

    [BindProperty]
    public Guid? Clinician { get; set; }

    [BindProperty]
    public string? Day { get; set; }

    [BindProperty]
    public string? Time { get; set; }

    [BindProperty]
    public int Minutes { get; set; } = 20;

    [BindProperty]
    public string? Reason { get; set; }

    public PatientHeader Header { get; private set; } = null!;

    public IReadOnlyList<(Guid Id, string Name)> Clinicians { get; private set; } = [];

    public IReadOnlyDictionary<string, string> Errors { get; private set; } = new Dictionary<string, string>();

    public Dictionary<string, string> ChartLink
    {
        get
        {
            var route = Search.RouteData();
            route["id"] = Header.Patient.Id.ToString();
            return route;
        }
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (await LoadAsync(id) is { } refused)
        {
            return refused;
        }

        Clinician = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        Day = clinic.Today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        Response.Headers.CacheControl = "no-store";
        if (await LoadAsync(id) is { } refused)
        {
            return refused;
        }

        var errors = new Dictionary<string, string>();
        if (Clinician is not { } clinician || Clinicians.All(c => c.Id != clinician))
        {
            errors["Clinician"] = "Choose a clinician.";
        }

        var start = AppointmentForm.Check(clinic, Day, Time, Minutes, Reason, errors);
        if (errors.Count == 0)
        {
            switch (await appointments.BookAsync(id, Clinician!.Value, start!.Value, Minutes, Reason!, HttpContext.RequestAborted))
            {
                case AppointmentResult.Saved:
                    return Redirect(Url.Page("/Patients/Chart", Search.ChartRouteValues(id)) + "#chart-appointments");
                case AppointmentResult.Overlap:
                    errors["Time"] = "This clinician already has an appointment booked then. Choose another time.";
                    break;
                default:
                    return RedirectToPage("/Patients/Chart", Search.ChartRouteValues(id));
            }
        }

        Errors = errors;
        Response.StatusCode = errors.ContainsKey("Time") && errors["Time"].StartsWith("This clinician", StringComparison.Ordinal)
            ? StatusCodes.Status409Conflict
            : StatusCodes.Status422UnprocessableEntity;
        return Page();
    }

    // The gate, then the one recorded read. Null when the form may be shown.
    async Task<IActionResult?> LoadAsync(Guid id)
    {
        if (!await assignments.IsOnPanelAsync(id, HttpContext.RequestAborted))
        {
            return RedirectToPage("/Patients/Chart", Search.ChartRouteValues(id));
        }

        if (await phi.HeaderAsync(id, HttpContext.RequestAborted) is not { } header)
        {
            return NotFound();
        }

        Header = header;
        Clinicians = await appointments.CliniciansAsync(HttpContext.RequestAborted);
        return null;
    }
}
