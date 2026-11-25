using System.Security.Claims;
using Ehr.Data.Messaging;
using Ehr.Domain.Accounts;
using Ehr.Domain.Messaging;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Portal.Pages.Messages;

// A patient writes to their care team (FR-18): a subject and a message, which starts a conversation. The patient
// comes from the session, never from the request. The database puts the message in the inbox of each clinician who
// has the patient on their panel. Off-site the post is forwarded to the clinic's server; when that can't be reached
// the cloud app's "Not saved" page shows what was typed again (UC 2, ShownIfNotSaved).
[ShownIfNotSaved(nameof(Subject), nameof(Body))]
public class NewModel(PortalSubject subject, SecureMessages messages) : PageModel
{
    [BindProperty]
    public string? Subject { get; set; }

    [BindProperty]
    public string? Body { get; set; }

    public IReadOnlyDictionary<string, string> Errors { get; private set; } = new Dictionary<string, string>();

    // Opened from a result's "Ask about this result": the subject is filled in, and the message box
    // has the focus.
    public bool About { get; private set; }

    public IActionResult OnGet()
    {
        Response.Headers.CacheControl = "no-store";
        // A proxy whose grant doesn't include messages (FR-19) is sent to the page that says so.
        return subject.MayMessage ? Page() : RedirectToPage("Index");
    }

    // The results page posts the subject here, so it stays out of the address. Nothing is saved: the form comes back
    // with the subject filled in, for the patient to write the message and send it. The subject is the patient's to
    // change, like any they type, and is cut to the length a subject may have.
    // A proxy whose grant doesn't include messages is sent to the page that says so, as from the form itself.
    public IActionResult OnPostAbout()
    {
        Response.Headers.CacheControl = "no-store";
        if (!subject.MayMessage)
        {
            return RedirectToPage("Index");
        }

        About = true;
        var typed = Subject?.Trim();
        Subject = typed?.Length > SecureMessages.MaximumSubject ? typed[..SecureMessages.MaximumSubject] : typed;
        Body = null;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Response.Headers.CacheControl = "no-store";
        if (!subject.MayMessage)
        {
            return RedirectToPage("Index");
        }

        var errors = SecureMessages.Check(Subject, Body, reply: false);
        if (errors.Count == 0)
        {
            var patientId = subject.PatientId;
            var (result, thread) = await messages.StartAsync(patientId, MessageSender.Patient, Subject!, Body!, HttpContext.RequestAborted);
            if (result == SendResult.Sent)
            {
                return RedirectToPage("Thread", new { threadId = thread, sent = true });
            }

            errors["Body"] = result == SendResult.TooMany ? SecureMessages.TooMany : "Your message couldn't be sent. Try again, or contact the clinic.";
        }

        Errors = errors;
        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return Page();
    }
}
