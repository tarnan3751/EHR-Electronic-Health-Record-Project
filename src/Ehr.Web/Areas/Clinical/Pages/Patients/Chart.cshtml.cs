using Ehr.Data.Patients;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Patients;

// A patient's chart: the demographics for now. A patient off the clinician's panel first asks for a reason (Reason,
// BreakGlass); then the read goes through the PHI reader, which records it, and a patient this clinician may not see
// is not found, which gives nothing away. From here a clinician puts the patient on their panel or takes them off it,
// and issues the patient a portal enrollment code.
[RequiresOnPrem]
public class ChartModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass, PatientEnrollments enrollments) : PageModel
{
    public Patient Patient { get; private set; } = null!;

    public bool OnPanel { get; private set; }

    // Set when the chart is open under emergency access: until when.
    public DateTimeOffset? EmergencyUntil { get; private set; }

    public string? EnrollmentCode { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id) => await LoadAsync(id) ?? Page();

    public async Task<IActionResult> OnPostAssignAsync(Guid id)
    {
        if (await LoadAsync(id) is { } refused)
        {
            return refused;
        }

        await assignments.AssignAsync(id, HttpContext.RequestAborted);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUnassignAsync(Guid id)
    {
        if (await LoadAsync(id) is { } refused)
        {
            return refused;
        }

        await assignments.EndAsync(id, HttpContext.RequestAborted);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostEnrollAsync(Guid id)
    {
        if (await LoadAsync(id) is { } refused)
        {
            return refused;
        }

        EnrollmentCode = await enrollments.IssueAsync(id, HttpContext.RequestAborted);
        return Page();
    }

    // The gate, then the read. Null when the chart may be shown; otherwise where to send the clinician instead.
    async Task<IActionResult?> LoadAsync(Guid id)
    {
        OnPanel = await assignments.IsOnPanelAsync(id, HttpContext.RequestAborted);
        if (!OnPanel)
        {
            EmergencyUntil = await breakGlass.ActiveUntilAsync(id, HttpContext.RequestAborted);
            if (EmergencyUntil is null)
            {
                return RedirectToPage("Reason", new { id });
            }
        }

        var patient = await phi.PatientAsync(id, HttpContext.RequestAborted);
        if (patient is null)
        {
            return NotFound();
        }

        Patient = patient;
        return null;
    }
}
