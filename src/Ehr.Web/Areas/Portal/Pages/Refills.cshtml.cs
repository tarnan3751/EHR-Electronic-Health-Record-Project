using System.Security.Cryptography;
using Ehr.Domain.Phi;
using Ehr.Domain.Refills;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Portal.Pages;

// Ask for a refill (the expansion plan's step 13d): the patient, or someone they share their record with whose access
// includes messages, asks the care team to renew one of the record's medications, with a short note if they like, and
// sees what became of their requests: waiting, approved with what was ordered, or declined with the care team's
// message. Which patient comes from the session, never from the request (AGENTS.md). A medication is named in the form
// by a reference the app protects for the record being looked at, never by its ID, and the database checks again that
// the session may ask about it. A proxy whose access doesn't include messages is sent to the record's home: asking is
// writing to the care team. Sensitive medications and requests are left out until asked for, the asking recorded, as
// everywhere. The read goes through the PHI reader; HEAD is refused. Off-site the page comes from the cloud copy and the post is
// forwarded to the clinic's server; while that can't be reached the form is replaced by a sentence, and the cloud app's
// "Not saved" page shows the note again (UC 2, ShownIfNotSaved).
[ShownIfNotSaved(nameof(Note))]
public class RefillsModel(PortalSubject subject, PhiReader phi, RefillRequests refills, IDataProtectionProvider protection) : PageModel
{
    public const int Limit = 50;

    public PatientRefills? Refills { get; private set; }

    // "Show sensitive items": a link, so the cloud app answers it itself, as a read.
    [BindProperty(SupportsGet = true)]
    public bool Sensitive { get; set; }

    // Set by the redirect after a request was sent.
    [BindProperty(SupportsGet = true)]
    public bool Sent { get; set; }

    // The protected reference of the medication chosen (Reference).
    [BindProperty]
    public string? Medication { get; set; }

    [BindProperty]
    public string? Note { get; set; }

    // The medication chosen in a post that was refused, to choose it again.
    public Guid? Chosen { get; private set; }

    // What to fix, by field: "Medication" or "Note".
    public IReadOnlyDictionary<string, string> Errors { get; private set; } = new Dictionary<string, string>();

    // The patient always; a proxy only when the patient allowed them sensitive items (FR-19).
    public bool MaySeeSensitive => subject.Acting is null || subject.Acting.IncludeSensitive;

    // Whose record this is, when it isn't the signed-in account's own.
    public bool Own => subject.Acting is null;

    // The medication's reference in the form: its ID, protected for the record the session is looking at, so the page
    // shows no ID and a reference made for one record means nothing under another.
    public string Reference(Guid medication) => Protector.Protect(medication.ToString("N"));

    IDataProtector Protector => protection.CreateProtector("Ehr.Portal.Refills.Medication", subject.PatientId.ToString("N"));

    public async Task<IActionResult> OnGetAsync()
    {
        // PHI: not kept by the browser, so Back after signing out on a shared computer doesn't show it again.
        Response.Headers.CacheControl = "no-store";
        if (!subject.MayMessage)
        {
            return RedirectToPage("/Index");
        }

        Refills = await phi.RefillsAsync(subject.PatientId, Sensitive && MaySeeSensitive, Limit, HttpContext.RequestAborted);
        return Page();
    }

    // HEAD would read the record, and record a view, for nothing shown: refused before anything is read, as step 13b's
    // pages refuse it.
    public IActionResult OnHead() => Record.IndexModel.GetOnly(Response);

    public async Task<IActionResult> OnPostAsync()
    {
        Response.Headers.CacheControl = "no-store";
        if (!subject.MayMessage)
        {
            return RedirectToPage("/Index");
        }

        Sent = false;
        var errors = new Dictionary<string, string>();
        Chosen = Unprotect(Medication);
        if (Chosen is not { } medication)
        {
            errors["Medication"] = "Choose a medication.";
        }
        else
        {
            switch (await refills.AskAsync(subject.PatientId, medication, Note, HttpContext.RequestAborted))
            {
                case RefillAsked.Sent:
                    // Back to the page as it was, sensitive items shown if they were.
                    return RedirectToPage(new { sent = true, sensitive = Sensitive && MaySeeSensitive ? true : (bool?)null });
                case RefillAsked.AlreadyAsked:
                    errors["Medication"] = "A request for this medication is already waiting.";
                    break;
                case RefillAsked.NoCareTeam:
                    errors["Medication"] = "This record has no care team yet.";
                    break;
                case RefillAsked.TooLong:
                    errors["Note"] = $"Note must be {RefillRequests.MaxNoteLength} characters or fewer.";
                    break;
                default:
                    Chosen = null;
                    errors["Medication"] = "That medication can no longer be refilled here.";
                    break;
            }
        }

        Errors = errors;
        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        Refills = await phi.RefillsAsync(subject.PatientId, Sensitive && MaySeeSensitive, Limit, HttpContext.RequestAborted);
        return Page();
    }

    // The medication a reference names, or null for one that wasn't made for this record or was changed.
    Guid? Unprotect(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        try
        {
            return Guid.TryParseExact(Protector.Unprotect(reference), "N", out var medication) ? medication : null;
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
