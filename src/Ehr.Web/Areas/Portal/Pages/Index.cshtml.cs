using System.Security.Claims;
using Ehr.Data.Patients;
using Ehr.Domain.Accounts;
using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Portal.Pages;

// The portal's home: the record the session is looking at, the account's own or one it holds a proxy grant for
// (PortalSubject). Which patient comes from the session, never from the request (AGENTS.md), and row-level security
// would return nothing else anyway. The read goes through the PHI reader.
public class IndexModel(PortalSubject subject, PhiReader phi) : PageModel
{
    public Patient? Patient { get; private set; }

    // The patient's next appointments (FR-17), read with the patient in the page's one recorded read.
    public IReadOnlyList<Ehr.Domain.Scheduling.AppointmentEntry> Upcoming { get; private set; } = [];

    // Set by the redirect when the proxy grant the session was acting under has ended (PortalSubjectFilter).
    [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)]
    public bool Ended { get; set; }

    // Whose record this is, when it isn't the signed-in account's own (FR-19).
    public string? ActingFor => subject.Acting?.PatientName;

    public bool MayMessage => subject.MayMessage;

    // Pre-visit forms that can be filled in now (step 13e): none for a proxy without sensitive items, who is shown no link.
    public int OpenForms { get; private set; }

    public async Task OnGetAsync()
    {
        // PHI: not kept by the browser, so Back after signing out on a shared computer doesn't show it again.
        Response.Headers.CacheControl = "no-store";
        var patientId = subject.PatientId;
        var appointments = await phi.AppointmentsAsync(patientId, past: 0, HttpContext.RequestAborted);
        Patient = appointments?.Patient;
        Upcoming = [.. (appointments?.Upcoming ?? []).Take(3)];
        if (Patient is not null && (subject.Acting is null || subject.Acting.IncludeSensitive))
        {
            OpenForms = await phi.OpenFormsAsync(patientId, HttpContext.RequestAborted);
        }
    }
}
