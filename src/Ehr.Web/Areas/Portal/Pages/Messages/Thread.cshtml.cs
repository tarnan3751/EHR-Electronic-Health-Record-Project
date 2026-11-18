using System.Security.Claims;
using Ehr.Data.Messaging;
using Ehr.Domain.Accounts;
using Ehr.Domain.Messaging;
using Ehr.Domain.Phi;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Portal.Pages.Messages;

// One of the patient's conversations (FR-18): its messages in order, and a reply. The address names the thread, never
// a patient: the patient comes from the session, and a thread that isn't theirs is not found. Opening it is a recorded
// read of the thread, and marks the care team's messages in it as read. Off-site it is read from the cloud copy, where
// nothing can be marked; a reply is forwarded to the clinic's server.
[ShownIfNotSaved(nameof(Body))]
public class ThreadModel(PhiReader phi, SecureMessages messages, SiteKind site) : PageModel
{
    [BindProperty]
    public string? Body { get; set; }

    // Set by the redirect after a message was sent.
    [BindProperty(SupportsGet = true)]
    public bool Sent { get; set; }

    public ThreadDetail Thread { get; private set; } = null!;

    public string? Error { get; private set; }

    Guid PatientId => Guid.Parse(User.FindFirstValue(EhrClaims.PatientId)!);

    public async Task<IActionResult> OnGetAsync(Guid threadId)
    {
        if (await LoadAsync(threadId) is { } refused)
        {
            return refused;
        }

        // Only where there is a database to write to: the cloud app reads a copy.
        if (site.TakesWrites && Thread.Unread.Count > 0)
        {
            await messages.MarkReadAsync(Thread.Unread, HttpContext.RequestAborted);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid threadId)
    {
        if (await LoadAsync(threadId) is { } refused)
        {
            return refused;
        }

        var errors = SecureMessages.Check(subject: null, Body, reply: true);
        if (errors.Count == 0)
        {
            if (await messages.ReplyAsync(threadId, PatientId, MessageSender.Patient, Body!, HttpContext.RequestAborted))
            {
                return RedirectToPage(new { threadId, sent = true });
            }

            errors["Body"] = "Your message couldn't be sent. Try again, or contact the clinic.";
        }

        Error = errors["Body"];
        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return Page();
    }

    async Task<IActionResult?> LoadAsync(Guid threadId)
    {
        Response.Headers.CacheControl = "no-store";
        if (await phi.ThreadAsync(threadId, PatientId, asPatient: true, HttpContext.RequestAborted) is not { } thread)
        {
            return NotFound();
        }

        Thread = thread;
        return null;
    }
}
