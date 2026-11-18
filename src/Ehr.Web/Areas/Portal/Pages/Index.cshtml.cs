using System.Security.Claims;
using Ehr.Data.Patients;
using Ehr.Domain.Accounts;
using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Portal.Pages;

// The portal's home: the patient's own record. Which patient comes from the session, never from the request
// (AGENTS.md), and row-level security would return nothing else anyway. The read goes through the PHI reader.
public class IndexModel(PhiReader phi) : PageModel
{
    public Patient? Patient { get; private set; }

    // The patient's next appointments (FR-17), read with the patient in the page's one recorded read.
    public IReadOnlyList<Ehr.Domain.Scheduling.AppointmentEntry> Upcoming { get; private set; } = [];

    public async Task OnGetAsync()
    {
        // PHI: not kept by the browser, so Back after signing out on a shared computer doesn't show it again.
        Response.Headers.CacheControl = "no-store";
        var patientId = Guid.Parse(User.FindFirstValue(EhrClaims.PatientId)!);
        var appointments = await phi.AppointmentsAsync(patientId, past: 0, HttpContext.RequestAborted);
        Patient = appointments?.Patient;
        Upcoming = [.. (appointments?.Upcoming ?? []).Take(3)];
    }
}
