using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Orders;

// UC 1 (Tahseen Arnan): a patient's discontinued orders, newest first: what each was, who discontinued it, when and
// why, and whether a changed order replaced it. Read-only, so it works off-site like the chart. Open to whoever the
// chart is open to: a clinician with the patient on their panel, or under emergency access; anyone else is sent to
// give a reason first, before anything is read. One recorded read of the patient, through the PHI reader.
public class DiscontinuedModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass) : PageModel
{
    // How many the page lists; it says so when there are more.
    public const int Limit = 100;

    [BindProperty(SupportsGet = true)]
    public SearchQuery Search { get; set; } = SearchQuery.None;

    public DiscontinuedOrders Orders { get; private set; } = null!;

    // For the banner, as on the chart: whether the patient is on the clinician's panel, or until when emergency access lasts.
    public bool OnPanel { get; private set; }

    public DateTimeOffset? EmergencyUntil { get; private set; }

    public Dictionary<string, string> ChartLink
    {
        get
        {
            var route = Search.RouteData();
            route["id"] = Orders.Patient.Id.ToString();
            return route;
        }
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Response.Headers.CacheControl = "no-store";
        OnPanel = await assignments.IsOnPanelAsync(id, HttpContext.RequestAborted);
        if (!OnPanel)
        {
            EmergencyUntil = await breakGlass.ActiveUntilAsync(id, HttpContext.RequestAborted);
            if (EmergencyUntil is null)
            {
                return RedirectToPage("/Patients/Reason", Search.ChartRouteValues(id));
            }
        }

        if (await phi.DiscontinuedOrdersAsync(id, Limit, HttpContext.RequestAborted) is not { } orders)
        {
            return NotFound();
        }

        Orders = orders;
        return Page();
    }
}
