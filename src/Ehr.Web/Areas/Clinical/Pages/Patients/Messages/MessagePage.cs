using Ehr.Domain.Patients;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Messages;

// What the clinician's message pages share (FR-18): who may open them and who may write. A thread is open to whoever
// the chart is open to, a clinician with the patient on their panel or under emergency access; only a panel clinician,
// one of the patient's care team, writes. Anyone else is sent to the chart, which asks for a reason, before anything is
// read.
public abstract class MessagePage(PatientAssignments assignments, BreakGlass breakGlass) : PageModel
{
    public bool OnPanel { get; private set; }

    protected async Task<IActionResult?> GateAsync(Guid patientId, bool writing)
    {
        Response.Headers.CacheControl = "no-store";
        OnPanel = await assignments.IsOnPanelAsync(patientId, HttpContext.RequestAborted);
        return OnPanel || (!writing && await breakGlass.ActiveUntilAsync(patientId, HttpContext.RequestAborted) is not null)
            ? null
            : RedirectToPage("/Patients/Chart", new { id = patientId });
    }
}
