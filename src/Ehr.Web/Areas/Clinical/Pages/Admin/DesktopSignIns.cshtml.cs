using System.Security.Claims;
using Ehr.Data.Accounts;
using Ehr.Domain.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Admin;

// One staff account's desktop sign-ins, for an administrator to revoke them all at once, the remote wipe (the expansion
// plan's step 20, proposed FR-69): each computer holding one deletes everything the desktop app keeps there, unsent
// drafts and recordings included, the next time it reaches a server (DesktopSessions, DesktopTokenHandler). All at once,
// since a computer holds only its latest token and each 12-hour sign-in makes a new one: sessions can't stand for
// computers. The page says what is lost before anything is done, and that revoking works before or after the account
// is disabled or its password or authenticator reset, which end these sign-ins but delete nothing: the app keeps a
// token the server refused for any of those, so a later revocation still reaches it (step 20's final review). The administrator's own account may be revoked too:
// it may be their laptop that is lost. Under the Admin folder's policy, like every page here. Off-site, the cloud app
// shows it from the replica and forwards the revocation to the clinic's server, and while it can't, says so.
public class DesktopSignInsModel(StaffAccounts accounts, DesktopSessions sessions) : PageModel
{
    public StaffUser Member { get; private set; } = null!;

    // Sign-ins not yet ended by a sign-out or a revocation, expired ones included.
    public int Unended { get; private set; }

    // Shown on the staff page, where this goes once done.
    [TempData]
    public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (await accounts.FindAsync(id) is not { } member)
        {
            return NotFound();
        }

        Member = member;
        Unended = await sessions.UnendedCountAsync(id, HttpContext.RequestAborted);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        if (await accounts.FindAsync(id) is not { } member)
        {
            return NotFound();
        }

        var administrator = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var revoked = await sessions.RevokeAllAsync(id, administrator, HttpContext.RequestAborted);
        Message = revoked == 0
            ? $"{member.DisplayName} had no desktop sign-in to revoke."
            : $"{member.DisplayName}'s desktop sign-ins are revoked. Each computer that holds one deletes what SwiftChart keeps there the next time it reaches a server.";
        return RedirectToPage("/Admin/Staff");
    }
}
