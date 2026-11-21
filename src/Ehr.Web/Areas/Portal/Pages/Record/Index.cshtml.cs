using Ehr.Domain.Phi;
using Ehr.Web.Fhir;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Portal.Pages.Record;

// "Download my record" (the expansion plan's step 13b): a copy of the record the session is looking at, the account's
// own or one it holds a proxy grant for, as a FHIR R4 file (OnGetFileAsync, Fhir/FhirRecord.cs) or as a page to print
// (Print). Which patient comes from the session, never from the request (AGENTS.md), and row-level security returns
// nothing else anyway. The page itself reads nothing of the record: it says what the copy holds, and that once it is
// downloaded or printed, where it goes is up to the person who has it. Both are plain GETs, so the cloud app answers
// them itself from its copy. What holds against another site: the sign-in cookie is SameSite=Strict, so a link from
// elsewhere arrives signed out and is sent to sign in; signing in goes on to this page without the link's query
// (Links.LocalPage), so never to the file or to sensitive items; and no answer has a CORS header, so no other site can
// read one. Sensitive items are left out unless asked for, for the patient and for a proxy the patient allowed them;
// for any other proxy there is nothing to ask for, and asking changes nothing.
public class IndexModel(PortalSubject subject, PhiReader phi) : PageModel
{
    // The most of each kind of entry the copy holds, newest first: as many as a search of the FHIR API returns.
    public const int Limit = 600;

    // "Include sensitive items": a box in the page's GET form, so the cloud app answers it too.
    [BindProperty(SupportsGet = true)]
    public bool Sensitive { get; set; }

    // The patient always; a proxy only when the patient allowed them sensitive items (FR-19).
    public bool MaySeeSensitive => subject.Acting is null || subject.Acting.IncludeSensitive;

    // Whose record this is, when it isn't the signed-in account's own.
    public string? ActingFor => subject.Acting?.PatientName;

    public bool Unavailable { get; private set; }

    public void OnGet() => Response.Headers.CacheControl = "no-store";

    // The file: one recorded download of the patient (PhiReader.DownloadRecordAsync).
    public async Task<IActionResult> OnGetFileAsync()
    {
        Response.Headers.CacheControl = "no-store";
        var at = DateTimeOffset.UtcNow;
        if (await phi.DownloadRecordAsync(subject.PatientId, Sensitive && MaySeeSensitive, Limit, HttpContext.RequestAborted) is not { } record)
        {
            Unavailable = true;
            return Page();
        }

        return new FhirRecordFile(FhirRecord.Bundle(record, at), FhirRecord.FileName(at));
    }
}
