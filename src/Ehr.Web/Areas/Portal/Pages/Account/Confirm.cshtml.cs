using Ehr.Domain.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Portal.Pages.Account;

// The link in the registration email. Confirming is a POST behind a button, so a mail scanner that opens links
// doesn't confirm on the patient's behalf, and so the cloud app forwards it to the on-prem app, which can write.
public class ConfirmModel(PatientRegistration registration) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "user")]
    public Guid UserId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    public bool? Confirmed { get; private set; }

    public void OnGet()
    {
        if (UserId == Guid.Empty || string.IsNullOrEmpty(Token))
        {
            Confirmed = false;
        }
    }

    public async Task OnPostAsync() =>
        Confirmed = UserId != Guid.Empty && !string.IsNullOrEmpty(Token)
            && await registration.ConfirmAsync(UserId, Token, HttpContext.RequestAborted);
}
