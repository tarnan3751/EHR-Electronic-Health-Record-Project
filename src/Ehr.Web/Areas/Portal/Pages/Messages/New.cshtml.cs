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

    public void OnGet() => Response.Headers.CacheControl = "no-store";

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
