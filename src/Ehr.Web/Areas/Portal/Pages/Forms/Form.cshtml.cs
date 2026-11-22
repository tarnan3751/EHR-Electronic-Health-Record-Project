using Ehr.Domain.Forms;
using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Portal.Pages.Forms;

// One pre-visit form (the expansion plan's step 13e): to fill in, in one sitting, while it is open, or its answers
// once sent, in the wording it was asked with and never with a score. The address names the form, never a patient: the
// patient comes from the session, and a form that isn't the session's to see is not found, the same way whether it
// doesn't exist, is another patient's or is held back from a proxy without sensitive items, and sending to it just the
// same. Sending to a form the session can see that isn't open (closed while it was being filled in, or moved to a later
// appointment), or that the database refuses, says plainly that the answers were not sent, and why in general terms:
// nothing was stored, so nobody was told (from the step's review). The database scores the answers and refuses anything
// the page let through (db/policies/85-pre-visit-forms.sql). Every form says it isn't watched as it arrives and where to
// get help now; an answer that needs review brings the help its question names to the top of the page, sent or not
// (FormNotices). Opening it is a view of sensitive items. Off-site it is read from the cloud copy, and sending is
// forwarded to the clinic's server, or not offered while that can't be reached.
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

    // Answers sent to a form that couldn't take them: nothing was stored.
    public bool NotSent { get; private set; }

    // The help notices the answers call for (FormNotices), by name: those sent, or those just refused.
    public IReadOnlyList<string> Help { get; private set; } = [];

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
        Help = HelpFor(form, form.Response?.Answers);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid requestId)
    {
        Response.Headers.CacheControl = "no-store";
        // Not the session's to see: one answer for all of them, so sending says nothing of which.
        if (await phi.PortalFormAsync(PatientId, requestId, HttpContext.RequestAborted) is not { } form)
        {
            return NotFound();
        }

        Form = form;
        // One already answered shows its answers; one that isn't open says the answers weren't sent.
        if (form.Response is not null)
        {
            return RedirectToPage(new { requestId });
        }

        if (!form.OpenAt(DateTimeOffset.UtcNow))
        {
            return Unsent(form);
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
            // Not sent either: the help an answer given calls for comes first all the same.
            Missing = missing;
            Help = HelpFor(form, chosen);
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            return Page();
        }

        return await forms.SendAsync(PatientId, requestId, chosen, HttpContext.RequestAborted) switch
        {
            FormResult.Saved => RedirectToPage(new { requestId, sent = true }),
            // Sent a moment ago, from another tab: its answers stand.
            FormResult.AlreadyDone => RedirectToPage(new { requestId }),
            // Closed by the database's clock a moment before this page's: the answers checked above are the form's.
            _ => Unsent(form),
        };
    }

    // The form again, saying the answers weren't sent, with the help any of them calls for.
    PageResult Unsent(FormEntry form)
    {
        NotSent = true;
        Help = HelpFor(form, Answers.Where(answer => answer.Value is not null).ToDictionary(answer => answer.Key, answer => answer.Value!, StringComparer.Ordinal));
        Response.StatusCode = StatusCodes.Status409Conflict;
        return Page();
    }

    // The help notices named by the questions whose answers need review (QuestionnaireDefinition.NeedingReview).
    static IReadOnlyList<string> HelpFor(FormEntry form, IReadOnlyDictionary<string, string>? answers) =>
        answers is null ? [] : [.. form.Definition.NeedingReview(answers).Select(question => question.Item.Help).OfType<string>().Distinct()];
}
