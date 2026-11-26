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

// A clinician starts a conversation with a patient on their panel (FR-18). The patient is told by email that there is
// a message, with a link to the portal and nothing about what it says.
[ShownIfNotSaved(nameof(Subject), nameof(Body))]
public class NewModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass, SecureMessages messages, IOptions<SiteAddresses> addresses)
    : MessagePage(assignments, breakGlass)
{
    [BindProperty]
    public string? Subject { get; set; }

    [BindProperty]
    public string? Body { get; set; }

    public PatientHeader Header { get; private set; } = null!;

    public IReadOnlyDictionary<string, string> Errors { get; private set; } = new Dictionary<string, string>();

    public async Task<IActionResult> OnGetAsync(Guid patientId) => await LoadAsync(patientId) ?? Page();

    public async Task<IActionResult> OnPostAsync(Guid patientId)
    {
        if (await LoadAsync(patientId) is { } refused)
        {
            return refused;
        }

        var errors = SecureMessages.Check(Subject, Body, reply: false);
        if (errors.Count == 0)
        {
            var (result, thread) = await messages.StartAsync(patientId, MessageSender.Staff, Subject!, Body!, HttpContext.RequestAborted);
            if (result == SendResult.Sent)
            {
                await messages.NotifyPatientAsync(patientId, await phi.UnreadFromStaffAsync(patientId, HttpContext.RequestAborted), Links.Build(addresses.Value.PortalBaseUrl, "/portal/messages"), HttpContext.RequestAborted);
                return RedirectToPage("Thread", new { patientId, threadId = thread, sent = true });
            }

            return RedirectToPage("/Patients/Chart", new { id = patientId });
        }

        Errors = errors;
        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return Page();
    }

    async Task<IActionResult?> LoadAsync(Guid patientId)
    {
        if (await GateAsync(patientId, writing: true) is { } refused)
        {
            return refused;
        }

        if (await phi.HeaderAsync(patientId, HttpContext.RequestAborted) is not { } header)
        {
            return NotFound();
        }

        Header = header;
        return null;
    }
}
