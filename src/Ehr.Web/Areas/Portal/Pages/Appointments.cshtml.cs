using System.Security.Claims;
using Ehr.Domain.Accounts;
using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Portal.Pages;

// The patient's appointments (FR-17): those still to come, soonest first, and earlier ones, latest first. Read-only:
// there is no self-scheduling. Which patient comes from the session, never from the request (AGENTS.md), and row-level
// security would return nothing else anyway. One recorded read of the patient, through the PHI reader; off-site it
// comes from the cloud copy like the rest of the portal.
public class AppointmentsModel(PhiReader phi) : PageModel
{
    public const int Past = 20;

    public PatientAppointments? Appointments { get; private set; }

    public async Task OnGetAsync()
    {
        Response.Headers.CacheControl = "no-store";
        var patientId = Guid.Parse(User.FindFirstValue(EhrClaims.PatientId)!);
        Appointments = await phi.AppointmentsAsync(patientId, Past, HttpContext.RequestAborted);
    }
}
