using Ehr.Data.Patients;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Patients;

// The clinician's patients: their own panel first, and a search across the clinic by name or MRN, with the patients
// already on their panel marked. Every patient listed is a PHI read, recorded by the PHI reader; row-level security
// decides which patients exist for this clinician.
[RequiresOnPrem]
public class IndexModel(PhiReader phi, PatientAssignments assignments) : PageModel
{
    public const int Limit = 100;

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    // The panel, when there is no search.
    public IReadOnlyList<Patient> PanelPatients { get; private set; } = [];

    // The search results, when there is one.
    public IReadOnlyList<Patient> Results { get; private set; } = [];

    // Who is on the panel, to mark in the results.
    public IReadOnlySet<Guid> Panel { get; private set; } = new HashSet<Guid>();

    public async Task OnGetAsync()
    {
        if (Q is { Length: > 0 })
        {
            Results = await phi.SearchPatientsAsync(Q, Limit, HttpContext.RequestAborted);
            Panel = await assignments.PanelIdsAsync(HttpContext.RequestAborted);
        }
        else
        {
            PanelPatients = await phi.PanelAsync(Limit, HttpContext.RequestAborted);
        }
    }
}
