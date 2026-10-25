using Ehr.Data.Patients;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Patients;

// A patient's chart: the demographics for now. The read goes through the PHI reader, which records it; a patient this
// clinician may not see is not found, which gives nothing away. From here a clinician issues the patient a portal
// enrollment code.
[RequiresOnPrem]
public class ChartModel(PhiReader phi, PatientEnrollments enrollments) : PageModel
{
    public Patient Patient { get; private set; } = null!;

    public string? EnrollmentCode { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id) => await LoadAsync(id) ? Page() : NotFound();

    public async Task<IActionResult> OnPostEnrollAsync(Guid id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        EnrollmentCode = await enrollments.IssueAsync(id, HttpContext.RequestAborted);
        return Page();
    }

    async Task<bool> LoadAsync(Guid id)
    {
        var patient = await phi.PatientAsync(id, HttpContext.RequestAborted);
        if (patient is null)
        {
            return false;
        }

        Patient = patient;
        return true;
    }
}
