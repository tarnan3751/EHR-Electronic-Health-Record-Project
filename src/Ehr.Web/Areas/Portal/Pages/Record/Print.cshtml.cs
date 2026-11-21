using Ehr.Data.Scheduling;
using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Portal.Pages.Record;

// The printable page of "Download my record" (the expansion plan's step 13b): the record the session is looking at, the
// same entries the file holds, and the visit summaries as the portal's summaries page shows them, laid out to print. The
// shared stylesheet's print rules take off everything that isn't the record. Which patient comes from the session, never
// from the request (AGENTS.md). Opening it is a view, recorded as the summaries page records one: a read of the patient
// (of their sensitive items when they were asked for) and a read of each summary shown (PhiReader.PrintableRecordAsync).
// Sensitive items are left out until asked for, as everywhere; for a proxy without them there are none, and asking
// changes nothing. Off-site it comes from the cloud copy.
public class PrintModel(PortalSubject subject, PhiReader phi) : PageModel
{
    // "Show sensitive items": a link, so the cloud app answers it too.
    [BindProperty(SupportsGet = true)]
    public bool Sensitive { get; set; }

    public PatientRecord? Record { get; private set; }

    // When the page was made, which it says, since it is a copy of the record at that moment.
    public DateTimeOffset At { get; private set; }

    // The patient always; a proxy only when the patient allowed them sensitive items (FR-19).
    public bool MaySeeSensitive => subject.Acting is null || subject.Acting.IncludeSensitive;

    public bool Own => subject.Acting is null;

    public async Task OnGetAsync()
    {
        // PHI: not kept by the browser, so Back after signing out on a shared computer doesn't show it again.
        Response.Headers.CacheControl = "no-store";
        At = DateTimeOffset.UtcNow;
        Record = await phi.PrintableRecordAsync(subject.PatientId, Sensitive && MaySeeSensitive, IndexModel.Limit, SummariesModel.Limit, HttpContext.RequestAborted);
    }

    // HEAD would read the record, and record a view, for nothing shown: refused before anything is read.
    public IActionResult OnHead() => IndexModel.GetOnly(Response);

    // A status as the record codes it ("on-hold"), as a person reads it ("On hold").
    public static string Said(string code) => code.Length == 0 ? code : char.ToUpperInvariant(code[0]) + code[1..].Replace('-', ' ');

    // An appointment's status as the portal's appointments page says it.
    public static string AppointmentSaid(string status) => status switch
    {
        AppointmentStatus.Fulfilled => "Attended",
        AppointmentStatus.NoShow => "Missed",
        AppointmentStatus.Cancelled => "Cancelled",
        _ => "Booked",
    };
}
