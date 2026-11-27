using Ehr.Data.Audit;
using Ehr.Domain.Audit;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Admin;

// One emergency access, for an administrator to review (the expansion plan's step 18; UC 24, proposed FR-64): who used
// it, the patient by ID, when, the reason given, and what was done under it, from the audit log, with nothing of the
// record; then whether it was appropriate, with a note, once it has ended, by an administrator who isn't the clinician.
// Under the Admin folder's policy, like every page here. Reached from the access's inbox item and from the audit page.
// The database holds the review to its rules (EmergencyAccessReviews); this page says why when it refuses. Off-site,
// the cloud app shows it from the replica and forwards the review to the clinic's server, and while it can't, says so;
// a note typed as the link drops is shown again on the "Not saved" page.
[ShownIfNotSaved(nameof(Note))]
public class EmergencyAccessModel(EmergencyAccessReviews reviews) : PageModel
{
    [BindProperty]
    public string? Outcome { get; set; }

    [BindProperty]
    public string? Note { get; set => field = value?.Trim(); }

    public EmergencyAccessForReview Access { get; private set; } = null!;

    // Why the review wasn't saved, for the page to say beside the form; or null.
    public string? Problem { get; private set; }

    public Dictionary<string, string> Errors { get; } = [];

    [TempData]
    public bool Reviewed { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Response.Headers.CacheControl = "no-store";
        if (await reviews.ReadAsync(id, HttpContext.RequestAborted) is not { } access)
        {
            return NotFound();
        }

        Access = access;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        Response.Headers.CacheControl = "no-store";
        if (await reviews.ReadAsync(id, HttpContext.RequestAborted) is not { } access)
        {
            return NotFound();
        }

        Access = access;
        if (Outcome is null || !EmergencyAccessOutcomes.All.Contains(Outcome))
        {
            Errors[nameof(Outcome)] = "Select whether it was appropriate.";
        }

        if (string.IsNullOrEmpty(Note))
        {
            Errors[nameof(Note)] = "Note is required.";
        }
        else if (Note.Length is < 10 or > 500)
        {
            Errors[nameof(Note)] = "Note must be 10 to 500 characters.";
        }

        if (Errors.Count > 0)
        {
            return Page();
        }

        var outcome = await reviews.ReviewAsync(id, Outcome!, Note!, HttpContext.RequestAborted);
        switch (outcome)
        {
            case ReviewOutcome.Reviewed:
                Reviewed = true;
                return RedirectToPage(new { id });
            case ReviewOutcome.NotFound:
                return NotFound();
            default:
                Problem = outcome switch
                {
                    ReviewOutcome.AlreadyReviewed => "Someone reviewed this access first.",
                    ReviewOutcome.NotEnded => "This access is still in force. Review it once it ends.",
                    ReviewOutcome.OwnAccess => "This is your own access. Another administrator reviews it.",
                    ReviewOutcome.NoteLength => "Note must be 10 to 500 characters.",
                    _ => "Select whether it was appropriate.",
                };
                Access = (await reviews.ReadAsync(id, HttpContext.RequestAborted))!;
                return Page();
        }
    }
}
