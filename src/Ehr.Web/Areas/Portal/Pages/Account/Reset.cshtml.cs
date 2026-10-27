using Ehr.Domain.Accounts;
using Ehr.Web.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Portal.Pages.Account;

// The link in the password reset email (PatientAccounts): the patient chooses a new password here. The token in the
// link works once and expires on its own. The account ID in the link names an account, never a patient.
public class ResetModel(PatientAccounts accounts) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "user")]
    public Guid UserId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    [BindProperty]
    public NewPassword Input { get; set; } = new();

    public bool LinkIsValid { get; private set; }

    public bool Done { get; private set; }

    public void OnGet() => LinkIsValid = UserId != Guid.Empty && !string.IsNullOrEmpty(Token);

    public async Task<IActionResult> OnPostAsync()
    {
        LinkIsValid = UserId != Guid.Empty && !string.IsNullOrEmpty(Token);
        if (!LinkIsValid || !ModelState.IsValid)
        {
            return Page();
        }

        var result = await accounts.ResetPasswordAsync(UserId, Token!, Input.Password!, HttpContext.RequestAborted);
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

        Done = true;
        return Page();
    }
}
