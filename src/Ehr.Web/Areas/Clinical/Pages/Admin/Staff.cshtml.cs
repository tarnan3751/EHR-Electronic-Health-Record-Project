using Ehr.Data.Accounts;
using Ehr.Domain.Accounts;
using Ehr.Web.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Ehr.Web.Areas.Clinical.Pages.Admin;

// Staff accounts, for admins: who there is, and creating one. A new account gets a one-time setup link, shown once,
// which the admin passes on; the person chooses their password with it and enrolls an authenticator at their first
// sign-in. Nothing here is PHI.
public class StaffModel(StaffAccounts accounts, IOptions<SiteAddresses> links) : PageModel
{
    public IReadOnlyList<StaffUser> Staff { get; private set; } = [];

    [BindProperty]
    public InviteForm Input { get; set; } = new();

    public string? SetupLink { get; private set; }

    public async Task OnGetAsync() => Staff = await accounts.ListAsync(HttpContext.RequestAborted);

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
        member.LockoutEnd is { } until && until > DateTimeOffset.UtcNow ? $"Locked until {until:u}"
        : member.PasswordHash is null ? "Invited, not set up"
        : member.TwoFactorEnabled ? "Active"
        : "Authenticator not set up";
}
