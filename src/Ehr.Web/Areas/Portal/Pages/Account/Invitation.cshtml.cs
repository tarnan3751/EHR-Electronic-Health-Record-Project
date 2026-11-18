using Ehr.Domain.Proxies;
using Ehr.Web.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Portal.Pages.Account;

// Accepting an invitation to the portal as someone's proxy (step 6b). The link in the email carries a code that works
// once and expires; opening it shows that the address is theirs, so the account is created with that address already
// confirmed, a name and a password, and no record of its own. What it can then see is whatever was shared with that
// address, and nothing else. An unknown, used or expired code gets one answer. Anonymous, like registration, and
// limited per client address the same way (AccountRateLimits).
public class InvitationModel(ProxyInvitations invitations) : PageModel
{
    [BindProperty]
    public InvitationForm Input { get; set; } = new();

    // "open": the form. "invalid", "has-account", "created": what to say instead.
    public string State { get; private set; } = "open";

    public async Task OnGetAsync(string? code)
    {
        Response.Headers.CacheControl = "no-store";
        Input.Code = code;
        State = await invitations.IsOpenAsync(code, HttpContext.RequestAborted) ? "open" : "invalid";
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Response.Headers.CacheControl = "no-store";
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var accepted = await invitations.AcceptAsync(Input.Code, Input.Name!, Input.Password!, HttpContext.RequestAborted);
        foreach (var error in accepted.Errors)
        {
            ModelState.AddModelError("", error);
        }

        State = accepted.Outcome switch
        {
            AcceptanceOutcome.Accepted => "created",
            AcceptanceOutcome.Invalid => "invalid",
            AcceptanceOutcome.HasAccount => "has-account",
            _ => "open",
        };
        return Page();
    }
}
