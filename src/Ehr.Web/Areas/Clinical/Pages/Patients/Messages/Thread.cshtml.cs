using Ehr.Data.Messaging;
using Ehr.Domain.Accounts;
using Ehr.Domain.Messaging;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Ehr.Web.Accounts;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Messages;

// One of a patient's conversations, for a clinician (FR-18): its messages in order and, for a clinician on the
// patient's care team, a reply. Opening it is a recorded read of the thread; for a care-team clinician it also marks
// the patient's messages read. A reply marks the thread's inbox items done for the whole care team (the database
// does that), and tells the patient by email that there is a message, with no detail.
[ShownIfNotSaved(nameof(Body))]
public class ThreadModel(
    PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass, SecureMessages messages, IOptions<SiteAddresses> addresses, SiteKind site)
    : MessagePage(assignments, breakGlass)
{
    [BindProperty]
    public string? Body { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Sent { get; set; }

    public ThreadDetail Thread { get; private set; } = null!;

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid patientId, Guid threadId)
    {
        if (await LoadAsync(patientId, threadId, writing: false) is { } refused)
        {
            return refused;
        }

        // Only where there is a database to write to, and only by the care team (the database refuses anyone else).
        if (site.TakesWrites && OnPanel && Thread.Unread.Count > 0)
        {
            await messages.MarkReadAsync(Thread.Unread, HttpContext.RequestAborted);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid patientId, Guid threadId)
    {
        if (await LoadAsync(patientId, threadId, writing: true) is { } refused)
        {
            return refused;
        }

        var errors = SecureMessages.Check(subject: null, Body, reply: true);
        if (errors.Count == 0 && await messages.ReplyAsync(threadId, patientId, MessageSender.Staff, Body!, HttpContext.RequestAborted) == SendResult.Sent)
        {
            await messages.NotifyPatientAsync(patientId, await phi.UnreadFromStaffAsync(patientId, HttpContext.RequestAborted), Links.Build(addresses.Value.PortalBaseUrl, Request, "/portal/messages"), HttpContext.RequestAborted);
            return RedirectToPage(new { patientId, threadId, sent = true });
        }

        Error = errors.GetValueOrDefault("Body") ?? "The reply couldn't be sent. Reload the page and try again.";
        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return Page();
    }

    async Task<IActionResult?> LoadAsync(Guid patientId, Guid threadId, bool writing)
    {
        if (await GateAsync(patientId, writing) is { } refused)
        {
            return refused;
        }

        if (await phi.ThreadAsync(threadId, patientId, asPatient: false, HttpContext.RequestAborted) is not { } thread)
        {
            return NotFound();
        }

        Thread = thread;
        return null;
    }
}
