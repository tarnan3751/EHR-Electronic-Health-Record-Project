using Ehr.Data.Proxies;
using Ehr.Domain.Accounts;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Ehr.Domain.Proxies;
using Ehr.Web.Accounts;
using Ehr.Web.Areas.Clinical.Pages.Patients.Messages;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Proxies;

// Who can use the portal for this patient (FR-19), for the clinic: the list, recording a proxy for the patient (a
// parent for a child, a carer the patient asked for at the desk), and removing one. Open to whoever the chart is open
// to; only a clinician with the patient on their panel records or removes, as with messages (MessagePage). A proxy the
// clinic records never sees sensitive items: only the patient allows that, in the portal, and the database refuses it
// from anyone else.
[ShownIfNotSaved(nameof(Email))]
public class IndexModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass, ProxyAccess access, ProxyInvitations invitations, IOptions<SiteAddresses> addresses)
    : MessagePage(assignments, breakGlass)
{
    [BindProperty]
    public string? Email { get; set; }

    [BindProperty]
    public string? Scope { get; set; } = ProxyScope.Record;

    [BindProperty]
    public int Days { get; set; } = 90;

    // Set by the redirect after a change: "recorded" or "removed".
    [BindProperty(SupportsGet = true)]
    public string? Done { get; set; }

    public Ehr.Data.Patients.Patient Patient { get; private set; } = null!;

    public IReadOnlyList<Ehr.Data.Patients.AllergyIntolerance> Allergies { get; private set; } = [];

    public IReadOnlyList<ProxyGrantEntry> Grants { get; private set; } = [];

    public IReadOnlyDictionary<string, string> Errors { get; private set; } = new Dictionary<string, string>();

    public async Task<IActionResult> OnGetAsync(Guid patientId) => await LoadAsync(patientId, writing: false) ?? Page();

    public async Task<IActionResult> OnPostAsync(Guid patientId)
    {
        if (await LoadAsync(patientId, writing: true) is { } refused)
        {
            return refused;
        }

        var errors = ProxyAccess.Check(Email, Scope, Days);
        if (errors.Count == 0)
        {
            var earlier = Grants.Where(g => g.RevokedAt is null && string.Equals(g.Email, Email!.Trim(), StringComparison.OrdinalIgnoreCase)).Select(g => g.Id);
            var (result, grant) = await access.GiveAsync(patientId, earlier, Email!, Scope!, includeSensitive: false, Days, HttpContext.RequestAborted);
            if (result == GiveResult.Given)
            {
                // The email's link lets someone with no portal account create one that sees only this record (step 6b).
                var code = await invitations.IssueAsync(grant, HttpContext.RequestAborted);
                await access.NotifyAsync(Email!, Links.Build(addresses.Value.PortalBaseUrl, "/portal/sign-in"),
                    Links.Build(addresses.Value.PortalBaseUrl, $"/portal/invitation?code={code}"), HttpContext.RequestAborted);
                return RedirectToPage(new { patientId, done = "recorded" });
            }

            errors["Email"] = "The proxy couldn't be recorded. Check the address and try again.";
        }

        Errors = errors;
        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid patientId, Guid grantId)
    {
        if (await LoadAsync(patientId, writing: true) is { } refused)
        {
            return refused;
        }

        return Grants.Any(g => g.Id == grantId && g.InForce) && await access.RevokeAsync(grantId, HttpContext.RequestAborted)
            ? RedirectToPage(new { patientId, done = "removed" })
            : NotFound();
    }

    async Task<IActionResult?> LoadAsync(Guid patientId, bool writing)
    {
        if (await GateAsync(patientId, writing) is { } refused)
        {
            return refused;
        }

        if (await phi.ProxyGrantsAsync(patientId, asPatient: false, HttpContext.RequestAborted) is not { } read)
        {
            return NotFound();
        }

        (Patient, Grants, Allergies) = read;
        return null;
    }
}
