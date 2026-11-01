using System.Globalization;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Domain.Accounts;
using Ehr.Domain.Audit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Admin;

// The audit log, for admins (AuditLog): what to review, and the emergency accesses that need it most. Read-only, and
// not PHI (patients appear by ID), so the cloud app serves it from the replica.
public class AuditModel(AuditLog log, StaffAccounts accounts) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Action { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? Actor { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? Patient { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? To { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Flagged { get; set; }

    // The older page starts before this moment.
    [BindProperty(SupportsGet = true)]
    public DateTimeOffset? Before { get; set; }

    public AuditPage Log { get; private set; } = new([], new Dictionary<Guid, string>(), false);

    public IReadOnlyList<StaffUser> Staff { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Staff = await accounts.ListAsync(HttpContext.RequestAborted);
        var filter = new AuditFilter(
            Flagged ? AuditActions.BreakGlass : Action,
            Actor,
            Patient,
            From is { } from ? new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
            Before ?? (To is { } to ? new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null));
        Log = await log.ReadAsync(filter, HttpContext.RequestAborted);
    }

    // The same filter, continued before the oldest event shown.
    public Dictionary<string, string> OlderRoute()
    {
        var route = new Dictionary<string, string>();
        if (Action is { Length: > 0 })
        {
            route["Action"] = Action;
        }

        if (Actor is { } actor)
        {
            route["Actor"] = actor.ToString();
        }

        if (Patient is { } patient)
        {
            route["Patient"] = patient.ToString();
        }

        if (From is { } from)
        {
            route["From"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (To is { } to)
        {
            route["To"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (Flagged)
        {
            route["Flagged"] = "true";
        }

        route["Before"] = Log.Events[^1].OccurredAt.ToString("o", CultureInfo.InvariantCulture);
        return route;
    }
}
