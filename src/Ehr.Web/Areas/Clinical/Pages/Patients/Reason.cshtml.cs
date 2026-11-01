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
[RequiresOnPrem]
public class ReasonModel(BreakGlass breakGlass, PatientAssignments assignments) : PageModel
{
    [BindProperty]
    [Required(ErrorMessage = "Say why you need this chart.")]
    [MinLength(BreakGlass.MinimumReasonLength, ErrorMessage = "Use at least 10 characters.")]
    [StringLength(BreakGlass.MaximumReasonLength, ErrorMessage = "Use 500 characters or fewer.")]
    public string? Reason { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id) =>
        await assignments.IsOnPanelAsync(id, HttpContext.RequestAborted) || await breakGlass.ActiveUntilAsync(id, HttpContext.RequestAborted) is not null
            ? RedirectToPage("Chart", new { id })
            : Page();

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            await breakGlass.GrantAsync(id, Reason!.Trim(), HttpContext.RequestAborted);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            // No patient has this ID.
            return NotFound();
        }

        return RedirectToPage("Chart", new { id });
    }
}
