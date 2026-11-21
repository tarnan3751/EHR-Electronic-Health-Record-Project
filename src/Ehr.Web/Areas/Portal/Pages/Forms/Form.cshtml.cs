using Ehr.Domain.Forms;
using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Portal.Pages.Forms;

// One pre-visit form (the expansion plan's step 13e): to fill in, in one sitting, while it is open, or its answers
// once sent, in the wording it was asked with and never with a score. The address names the form, never a patient: the
// patient comes from the session, and a form that isn't the session's to see is not found, the same way whether it
// doesn't exist, is another patient's or is held back from a proxy without sensitive items. Sending is answered the
// same way for a form that isn't open. The database scores the answers and refuses anything the page let through
// (db/policies/85-pre-visit-forms.sql). Every form says it isn't watched as it arrives and where to get help now; an
// answer about self-harm brings that to the top of the page (FormNotices). Opening it is a view of sensitive items. Off-site
// it is read from the cloud copy, and sending is forwarded to the clinic's server, or not offered while that can't be
// reached.
public class FormModel(PortalSubject subject, PhiReader phi, PreVisitForms forms) : PageModel
{
    // Each question's LOINC code and the code of the answer chosen, as the radio groups send them.
    [BindProperty]
    public Dictionary<string, string?> Answers { get; set; } = [];

    // Set by the redirect after the answers were sent.
    [BindProperty(SupportsGet = true)]
    public bool Sent { get; set; }

    public FormEntry Form { get; private set; } = null!;

    // The questions sent without one of their answers, by code.
    public IReadOnlySet<string> Missing { get; private set; } = new HashSet<string>();

    // Whose record this is, when it isn't the signed-in account's own.
    public string? ActingFor => subject.Acting?.PatientName;

    Guid PatientId => subject.PatientId;

    public async Task<IActionResult> OnGetAsync(Guid requestId)
    {
        Response.Headers.CacheControl = "no-store";
        if (await phi.PortalFormAsync(PatientId, requestId, HttpContext.RequestAborted) is not { } form)
        {
            return NotFound();
        }

        Form = form;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid requestId)
    {
        Response.Headers.CacheControl = "no-store";
        // Not the session's to see, or not open to answers now: one answer for all of them, so sending says nothing of
        // which. One already answered shows its answers.
        if (await phi.PortalFormAsync(PatientId, requestId, HttpContext.RequestAborted) is not { } form
            || (form.Response is null && !form.OpenAt(DateTimeOffset.UtcNow)))
        {
            return NotFound();
        }

        Form = form;
        if (form.Response is not null)
        {
            return RedirectToPage(new { requestId });
        }

        // Each question answered with one of its own answers; an optional one may be left. Anything else is asked again,
        // with the answers given kept.
        var chosen = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in form.Definition.Items)
        {
            var given = Answers.GetValueOrDefault(item.Code);
            if (given is not null && form.Definition.AnswersTo(item).Any(answer => answer.Code == given))
            {
                chosen[item.Code] = given;
            }
            else if (!item.Optional)
            {
                missing.Add(item.Code);
            }
        }

        if (missing.Count > 0)
        {
            Missing = missing;
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            return Page();
        }

        return await forms.SendAsync(PatientId, requestId, chosen, HttpContext.RequestAborted) switch
        {
            FormResult.Saved => RedirectToPage(new { requestId, sent = true }),
            // Sent a moment ago, from another tab: its answers stand.
            FormResult.AlreadyDone => RedirectToPage(new { requestId }),
            _ => NotFound(),
        };
    }
}
