using System.Security.Claims;
using Ehr.Domain.Accounts;
using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Portal.Pages.Messages;

// The patient's conversations with their care team (FR-18), the latest first, with those that have something new
// marked. Which patient comes from the session, never from the request (AGENTS.md), and row-level security would
// return nothing else anyway. One recorded read of the patient; off-site it comes from the cloud copy.
public class IndexModel(PortalSubject subject, PhiReader phi) : PageModel
{
    public const int Limit = 50;

    public PatientThreads? Threads { get; private set; }

    // False for a proxy whose grant doesn't include messages (FR-19): nothing is read, and the page says so.
    public bool Allowed => subject.MayMessage;

    public async Task OnGetAsync()
    {
        Response.Headers.CacheControl = "no-store";
        if (!Allowed)
        {
            return;
        }

        var patientId = subject.PatientId;
        Threads = await phi.ThreadsAsync(patientId, asPatient: true, Limit, HttpContext.RequestAborted);
    }
}
