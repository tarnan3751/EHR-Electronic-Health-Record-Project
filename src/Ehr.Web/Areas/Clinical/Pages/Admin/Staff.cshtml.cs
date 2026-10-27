using System.Security.Claims;
using Ehr.Data.Accounts;
using Ehr.Domain.Accounts;
using Ehr.Web.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Ehr.Web.Areas.Clinical.Pages.Admin;

// Staff accounts, for admins: who there is, creating one, and looking after one. A new account gets a one-time setup
// link, shown once, which the admin passes on; the person chooses their password with it and enrolls an authenticator
// at their first sign-in. The same link serves a forgotten password; a lost phone gets the authenticator reset; and a
// person who leaves has the account disabled. An admin does none of these to their own account. Nothing here is PHI.
public class StaffModel(StaffAccounts accounts, IOptions<SiteAddresses> links) : PageModel
{
    public IReadOnlyList<StaffUser> Staff { get; private set; } = [];

    [BindProperty]
    public InviteForm Input { get; set; } = new();

    public string? SetupLink { get; private set; }

    [TempData]
    public string? Message { get; set; }

    public Guid CurrentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task OnGetAsync() => Staff = await accounts.ListAsync(HttpContext.RequestAborted);

    public Task<IActionResult> OnPostReissueAsync(Guid id) => ActOnAsync(id, async member =>
    {
        var reissued = await accounts.ReissueAsync(member, HttpContext.RequestAborted);
        if (!reissued.Result.Succeeded)
        {
            return reissued.Result.Errors.First().Description;
        }

        // The account's old password, if it had one, has stopped working; the link is the only way back in.
        SetupLink = Links.Build(links.Value.ClinicalBaseUrl, Request,
            $"/sign-in/setup?user={member.Id}&token={Uri.EscapeDataString(reissued.Token!)}");
        return null;
    });

    public Task<IActionResult> OnPostResetAuthenticatorAsync(Guid id) => ActOnAsync(id, async member =>
    {
        await accounts.ResetAuthenticatorAsync(member, HttpContext.RequestAborted);
        Message = $"{member.DisplayName} will set up an authenticator app again at their next sign-in.";
        return null;
    });

    public Task<IActionResult> OnPostDisableAsync(Guid id) => ActOnAsync(id, async member =>
    {
        await accounts.DisableAsync(member, HttpContext.RequestAborted);
        Message = $"{member.DisplayName} is disabled and can no longer sign in.";
        return null;
    });

    public Task<IActionResult> OnPostEnableAsync(Guid id) => ActOnAsync(id, async member =>
    {
        await accounts.EnableAsync(member, HttpContext.RequestAborted);
        Message = $"{member.DisplayName} can sign in again.";
        return null;
    });

    // An action on another account. The action returns a problem to show, or null when it's done; a setup link to show
    // stays on this page, since it's shown once, and anything else redirects, so a refresh repeats nothing.
    async Task<IActionResult> ActOnAsync(Guid id, Func<StaffUser, Task<string?>> action)
    {
        ModelState.Clear();
        var member = id == CurrentUserId ? null : await accounts.FindAsync(id);
        if (member is null)
        {
            ModelState.AddModelError("", "That account is not one you can change here.");
        }
        else if (await action(member) is { } problem)
        {
            ModelState.AddModelError("", problem);
        }
        else if (SetupLink is null)
        {
            return RedirectToPage();
        }

        Staff = await accounts.ListAsync(HttpContext.RequestAborted);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var roles = new List<string>();
        if (Input.Clinician)
        {
            roles.Add(StaffRoles.Clinician);
        }

        if (Input.Admin)
        {
            roles.Add(StaffRoles.Admin);
        }

        if (roles.Count == 0)
        {
            ModelState.AddModelError("", "Choose at least one role.");
        }

        if (ModelState.IsValid)
        {
            var invitation = await accounts.InviteAsync(Input.Email!, Input.DisplayName!, roles, HttpContext.RequestAborted);
            if (invitation.Result.Succeeded)
            {
                SetupLink = Links.Build(links.Value.ClinicalBaseUrl, Request,
                    $"/sign-in/setup?user={invitation.User!.Id}&token={Uri.EscapeDataString(invitation.Token!)}");
                ModelState.Clear();
                Input = new InviteForm();
            }
            else
            {
                foreach (var error in invitation.Result.Errors)
                {
                    ModelState.AddModelError("", error.Description);
                }
            }
        }

        Staff = await accounts.ListAsync(HttpContext.RequestAborted);
        return Page();
    }

    public static string StatusOf(StaffUser member) =>
        StaffAccounts.IsDisabled(member) ? "Disabled"
        : member.LockoutEnd is { } until && until > DateTimeOffset.UtcNow ? $"Locked until {until:u}"
        : member.PasswordHash is null ? "Invited, not set up"
        : member.TwoFactorEnabled ? "Active"
        : "Authenticator not set up";
}
