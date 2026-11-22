using Ehr.Domain.Accounts;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Ehr.Domain.Refills;
using Ehr.Web.Accounts;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Refills;

// A refill request (the expansion plan's step 13d), from the inbox of a clinician with the patient on their panel: the
// patient's banner with their allergies, the medication asked for as the chart has it now, who asked, when and with
// what note, and the two answers. "Approve: write the order" opens the order form for it, every check of an order with
// it; "Decline request" needs a message for the patient. Once answered, the page says how, by whom and when. One
// recorded read of the patient through the PHI reader, a view of their sensitive items when the request is sensitive;
// HEAD is refused before anything is read.
//
// The order form's rule: only for a patient on the clinician's panel, who are those the request reached; anyone else is
// sent to the chart before anything is read, which asks for a reason when the patient isn't on their panel. Whoever
// answers first answers it: the database refuses a second answer, and the page says so. A medication stopped or entered
// in error since, or no longer coded as a product the clinic orders, can't be renewed, so the page offers only Decline.
// Off-site the cloud app forwards the decline; while the clinic's server can't be reached the answers are replaced by a
// sentence, and the "Not saved" page shows the message again to copy (UC 2, ShownIfNotSaved).
[ShownIfNotSaved(nameof(Message))]
public class ReviewModel(PhiReader phi, PatientAssignments assignments, RefillRequests refills, IOptions<SiteAddresses> addresses) : PageModel
{
    public RefillReview Refill { get; private set; } = null!;

    // The message declining it.
    [BindProperty]
    public string? Message { get; set; }

    public string? Error { get; private set; }

    // Set when someone else answered first.
    public bool AlreadyAnswered { get; private set; }

    // How the request was just answered, from the redirect: "approved" or "declined".
    [BindProperty(SupportsGet = true)]
    public string? Answered { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid patientId, Guid requestId)
    {
        Response.Headers.CacheControl = "no-store";
        return await LoadAsync(patientId, requestId) ?? Page();
    }

    // HEAD would read the patient, and record a view, for nothing shown: refused before anything is read, as step 13b's
    // pages refuse it.
    public IActionResult OnHead() => Portal.Pages.Record.IndexModel.GetOnly(Response);

    public async Task<IActionResult> OnPostDeclineAsync(Guid patientId, Guid requestId)
    {
        Response.Headers.CacheControl = "no-store";
        if (await LoadAsync(patientId, requestId) is { } refused)
        {
            return refused;
        }

        switch (await refills.DeclineAsync(patientId, requestId, Message, HttpContext.RequestAborted))
        {
            case RefillDeclined.Declined:
                // The link is the configured portal's home, never built from this request (step 6c), and names nothing.
                await refills.NotifyAnsweredAsync(patientId, Links.Build(addresses.Value.PortalBaseUrl, "/portal"), HttpContext.RequestAborted);
                return RedirectToPage(new { patientId, requestId, answered = "declined" });
            case RefillDeclined.AlreadyAnswered:
                AlreadyAnswered = true;
                Response.StatusCode = StatusCodes.Status409Conflict;
                await LoadAsync(patientId, requestId);
                return Page();
            case RefillDeclined.NotAllowed:
                return RedirectToPage("/Patients/Chart", new { id = patientId });
            case RefillDeclined.TooLong:
                Error = $"Shorten the message to {RefillRequests.MaxDeclineLength:N0} characters or fewer.";
                break;
            default:
                Error = "Write a message for the patient: it is what they read in the portal.";
                break;
        }

        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return Page();
    }

    // The gate, then the one recorded read. Null when the page may be shown.
    async Task<IActionResult?> LoadAsync(Guid patientId, Guid requestId)
    {
        if (!await assignments.IsOnPanelAsync(patientId, HttpContext.RequestAborted))
        {
            return RedirectToPage("/Patients/Chart", new { id = patientId });
        }

        if (await phi.RefillRequestAsync(patientId, requestId, HttpContext.RequestAborted) is not { } request)
        {
            return NotFound();
        }

        Refill = request;
        return null;
    }
}
