using System.ComponentModel.DataAnnotations;
using Ehr.Domain.Accounts;
using Ehr.Web.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Ehr.Web.Areas.Portal.Pages.Account;

// A patient who has forgotten their password asks for a reset link (PatientAccounts). The answer is the same whether or
// not the address has an account.
public class ForgotModel(PatientAccounts accounts, IOptions<SiteAddresses> links) : PageModel
{
    [BindProperty]
    [Required(ErrorMessage = "Enter your email address.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    public string? Email { get; set; }

    public bool Sent { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        await accounts.RequestPasswordResetAsync(Email!,
            (userId, token) => Links.Build(links.Value.PortalBaseUrl, Request, $"/portal/reset?user={userId}&token={Uri.EscapeDataString(token)}"),
            HttpContext.RequestAborted);
        Sent = true;
        return Page();
    }
}
