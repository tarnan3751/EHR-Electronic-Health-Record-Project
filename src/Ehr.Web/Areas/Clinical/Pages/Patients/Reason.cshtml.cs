using System.ComponentModel.DataAnnotations;
using Ehr.Domain.Patients;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ehr.Web.Areas.Clinical.Pages.Patients;

// Where the chart sends a clinician whose patient this isn't: the stated reason emergency access requires (BreakGlass).
// Nothing about the patient is shown here; the search results the clinician came from already were a recorded read.
// That search travels along to the chart (SearchQuery), for the way back to it. Off-site with the clinic's server
// unreachable, the reason typed is shown again on the "Not saved" page (UC 2). The reason is kept trimmed, and its
// length is checked again once trimmed: model binding checks the value as posted, so spaces around a short reason would
// otherwise make it long enough.
//
// A chart the clinician may already open (on their panel, or under a grant that hasn't ended) goes straight to the
// chart, on a post as on a get: a reason sent twice, by a double click or by Back and resend, records one grant, not two.
[ShownIfNotSaved(nameof(Reason))]
public class ReasonModel(BreakGlass breakGlass, PatientAssignments assignments) : PageModel
{
    // The length messages, once each: model binding and the trimmed check below say the same thing.
    const string TooShort = "Use at least 10 characters.";
    const string TooLong = "Use 500 characters or fewer.";

    [BindProperty(SupportsGet = true)]
    public SearchQuery Search { get; set; } = SearchQuery.None;

    [BindProperty]
    [Required(ErrorMessage = "Say why you need this chart.")]
    [MinLength(BreakGlass.MinimumReasonLength, ErrorMessage = TooShort)]
    [StringLength(BreakGlass.MaximumReasonLength, ErrorMessage = TooLong)]
    public string? Reason { get; set => field = value?.Trim(); }

    public async Task<IActionResult> OnGetAsync(Guid id) =>
        await MayOpenAsync(id) ? RedirectToPage("Chart", Search.ChartRouteValues(id)) : Page();

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        if (await MayOpenAsync(id))
        {
            return RedirectToPage("Chart", Search.ChartRouteValues(id));
        }

        if (ModelState.IsValid && Reason!.Length < BreakGlass.MinimumReasonLength)
        {
            ModelState.AddModelError(nameof(Reason), TooShort);
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            await breakGlass.GrantAsync(id, Reason!, HttpContext.RequestAborted);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            // No patient has this ID.
            return NotFound();
        }

        return RedirectToPage("Chart", Search.ChartRouteValues(id));
    }

    // On the panel, or under emergency access that hasn't ended. The panel is checked first: it is the usual case, and
    // the grant's query is only needed off the panel.
    async Task<bool> MayOpenAsync(Guid id) =>
        await assignments.IsOnPanelAsync(id, HttpContext.RequestAborted)
        || await breakGlass.ActiveUntilAsync(id, HttpContext.RequestAborted) is not null;
}
