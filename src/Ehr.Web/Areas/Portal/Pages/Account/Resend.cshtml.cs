using System.ComponentModel.DataAnnotations;
using Ehr.Domain.Accounts;
using Ehr.Web.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Ehr.Web.Areas.Portal.Pages.Account;

// UC5 "Register for a portal account", its expired-link course (Simar Rekhi): a patient whose confirmation link expired
// asks for a new one (PatientRegistration.ResendConfirmationAsync). The answer is the same whether or not the address
// has an account waiting to be confirmed. A POST, so the cloud app forwards it to the on-prem app, which can write.
public class ResendModel(PatientRegistration registration, IOptions<SiteAddresses> links) : PageModel
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

        await registration.ResendConfirmationAsync(Email!,
            (userId, token) => Links.Build(links.Value.PortalBaseUrl, $"/portal/confirm?user={userId}&token={Uri.EscapeDataString(token)}"),
            HttpContext.RequestAborted);
        Sent = true;
        return Page();
    }
}
