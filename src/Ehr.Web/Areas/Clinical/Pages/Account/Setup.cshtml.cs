using Ehr.Domain.Accounts;
using Ehr.Web.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Account;

// The setup link an admin hands a new staff member (Admin/Staff): the account chooses its first password here. The
// token in the link works once and expires on its own.
public class SetupModel(StaffAccounts accounts) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "user")]
    public Guid UserId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    [BindProperty]
    public NewPassword Input { get; set; } = new();

    [TempData]
    public string? Message { get; set; }

    public bool LinkIsValid { get; private set; }

    public string? DisplayName { get; private set; }

    public async Task OnGetAsync()
    {
        var user = string.IsNullOrEmpty(Token) ? null : await accounts.FindAsync(UserId);
        LinkIsValid = user is { PasswordHash: null };
        DisplayName = user?.DisplayName;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = string.IsNullOrEmpty(Token) ? null : await accounts.FindAsync(UserId);
        LinkIsValid = user is { PasswordHash: null };
        DisplayName = user?.DisplayName;
        if (!LinkIsValid || !ModelState.IsValid)
        {
            return Page();
        }

        var result = await accounts.SetPasswordAsync(user!, Token!, Input.Password!, HttpContext.RequestAborted);
        if (!result.Succeeded)
        {
            // An invalid token reads the same as a used one: the link is spent.
            LinkIsValid = !result.Errors.Any(error => error.Code == "InvalidToken");
            foreach (var error in result.Errors.Where(error => error.Code != "InvalidToken"))
            {
                ModelState.AddModelError("", error.Description);
            }

            return Page();
        }

        Message = "Your password is set. Sign in to continue.";
        return RedirectToPage("SignIn");
    }
}
