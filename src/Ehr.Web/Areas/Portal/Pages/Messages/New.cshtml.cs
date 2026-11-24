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
public class NewModel(SecureMessages messages) : PageModel
{
    [BindProperty]
    public string? Subject { get; set; }

    [BindProperty]
    public string? Body { get; set; }

    public IReadOnlyDictionary<string, string> Errors { get; private set; } = new Dictionary<string, string>();

    // Opened from a result's "Ask your care team about this result": the subject is filled in, and the message box
    // has the focus.
    public bool About { get; private set; }

    public void OnGet() => Response.Headers.CacheControl = "no-store";

    // The results page posts the subject here, so it stays out of the address. Nothing is saved: the form comes back
    // with the subject filled in, for the patient to write the message and send it. The subject is the patient's to
    // change, like any they type, and is cut to the length a subject may have.
    public void OnPostAbout()
    {
        Response.Headers.CacheControl = "no-store";
        About = true;
        var subject = Subject?.Trim();
        Subject = subject?.Length > SecureMessages.MaximumSubject ? subject[..SecureMessages.MaximumSubject] : subject;
        Body = null;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Response.Headers.CacheControl = "no-store";
        var errors = SecureMessages.Check(Subject, Body, reply: false);
        if (errors.Count == 0)
        {
            var patientId = Guid.Parse(User.FindFirstValue(EhrClaims.PatientId)!);
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
