using Ehr.Data.Proxies;
using Ehr.Domain.Accounts;
using Ehr.Domain.Phi;
using Ehr.Domain.Proxies;
using Ehr.Web.Accounts;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Ehr.Web.Areas.Portal.Pages;

// Proxy access in the portal (FR-19): the records the signed-in account can view for someone else, and who can view
// the account's own record, with giving, changing and removing that access. The patient is the session's own, never
// from the request; a grant is named by its ID and found only among that patient's. While the session is looking at
// someone else's record nothing here can be changed: a proxy never passes access on.
[ShownIfNotSaved(nameof(Email))]
public class AccessModel(PhiReader phi, ProxyAccess access, PortalSubject subject, IOptions<SiteAddresses> addresses) : PageModel
{
    [BindProperty]
    public string? Email { get; set; }

    [BindProperty]
    public string? Scope { get; set; } = ProxyScope.Record;

    [BindProperty]
    public bool IncludeSensitive { get; set; }

    [BindProperty]
    public int Days { get; set; } = 90;

    // Set by the redirect after a change: "given", "removed" or "changed".
    [BindProperty(SupportsGet = true)]
    public string? Done { get; set; }

    public bool Acting => subject.Acting is not null;

    public IReadOnlyList<ProxiedRecord> Records { get; private set; } = [];

    public IReadOnlyList<ProxyGrantEntry> Grants { get; private set; } = [];

    public IReadOnlyDictionary<string, string> Errors { get; private set; } = new Dictionary<string, string>();

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        if (Acting)
        {
            return RedirectToPage();
        }

        await LoadAsync();
        var errors = ProxyAccess.Check(Email, Scope, Days);
        if (errors.Count == 0)
        {
            // Giving it again replaces what that person had.
            var earlier = Grants.Where(g => g.RevokedAt is null && string.Equals(g.Email, Email!.Trim(), StringComparison.OrdinalIgnoreCase)).Select(g => g.Id);
            switch (await access.GiveAsync(subject.OwnPatientId, earlier, Email!, Scope!, IncludeSensitive, Days, HttpContext.RequestAborted))
            {
                case GiveResult.Given:
                    await access.NotifyAsync(Email!, Links.Build(addresses.Value.PortalBaseUrl, Request, "/portal/access"), HttpContext.RequestAborted);
                    return RedirectToPage(new { done = "given" });
                case GiveResult.Yourself:
                    errors["Email"] = "That's your own email address. Enter the other person's.";
                    break;
                case GiveResult.TooMany:
                    errors["Email"] = "You've given access to as many people as the portal allows. Remove one first.";
                    break;
                default:
                    errors["Email"] = "Access couldn't be given. Try again, or contact the clinic.";
                    break;
            }
        }

        Errors = errors;
        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid grantId) =>
        await ChangeAsync(grantId, () => access.RevokeAsync(grantId, HttpContext.RequestAborted), "removed");

    public async Task<IActionResult> OnPostSensitiveAsync(Guid grantId, bool allow) =>
        await ChangeAsync(grantId, () => access.SetSensitiveAsync(grantId, allow, HttpContext.RequestAborted), "changed");

    // A change to one of the patient's own grants that is still in force; anything else is not found.
    async Task<IActionResult> ChangeAsync(Guid grantId, Func<Task<bool>> change, string done)
    {
        if (Acting)
        {
            return RedirectToPage();
        }

        await LoadAsync();
        return Grants.Any(g => g.Id == grantId && g.InForce) && await change() ? RedirectToPage(new { done }) : NotFound();
    }

    async Task LoadAsync()
    {
        Response.Headers.CacheControl = "no-store";
        Records = await phi.ProxiedRecordsAsync(HttpContext.RequestAborted);
        if (!Acting && await phi.ProxyGrantsAsync(subject.OwnPatientId, asPatient: true, HttpContext.RequestAborted) is { } own)
        {
            Grants = own.Grants;
        }
    }
}
