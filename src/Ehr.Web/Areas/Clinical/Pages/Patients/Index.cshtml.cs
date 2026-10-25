using Ehr.Data.Patients;
using Ehr.Domain.Phi;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Patients;

// Finding a patient, by name or MRN. Every result is a PHI read, recorded by the PHI reader; row-level security
// decides which patients exist for this clinician.
[RequiresOnPrem]
public class IndexModel(PhiReader phi) : PageModel
{
    public const int Limit = 50;

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public IReadOnlyList<Patient> Results { get; private set; } = [];

    public async Task OnGetAsync()
    {
        if (Q is { Length: > 0 })
        {
            Results = await phi.SearchPatientsAsync(Q, Limit, HttpContext.RequestAborted);
        }
    }
}
