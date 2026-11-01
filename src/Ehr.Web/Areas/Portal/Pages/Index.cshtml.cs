using System.Security.Claims;
using Ehr.Data.Patients;
using Ehr.Domain.Accounts;
using Ehr.Domain.Phi;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Portal.Pages;

// The portal's home: the patient's own record. Which patient comes from the session, never from the request
// (AGENTS.md), and row-level security would return nothing else anyway. The read goes through the PHI reader.
[RequiresOnPrem]
public class IndexModel(PhiReader phi) : PageModel
{
    public Patient? Patient { get; private set; }

    public async Task OnGetAsync()
    {
        var patientId = Guid.Parse(User.FindFirstValue(EhrClaims.PatientId)!);
        Patient = await phi.PatientAsync(patientId, HttpContext.RequestAborted);
    }
}
