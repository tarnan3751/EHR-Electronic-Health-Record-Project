using Ehr.Domain.Accounts;
using Ehr.Web.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Ehr.Web.Areas.Portal.Pages.Account;

// A patient's self-registration: the enrollment code from the clinic says which patient they are, and the
// confirmation email proves the address is theirs (PatientRegistration).
public class RegisterModel(PatientRegistration registration, IOptions<SiteAddresses> links) : PageModel
{
    [BindProperty]
    public RegisterForm Input { get; set; } = new();

    public bool Registered { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await registration.RegisterAsync(Input.Email!, Input.Password!, Input.Code!,
            (userId, token) => Links.Build(links.Value.PortalBaseUrl, Request, $"/portal/confirm?user={userId}&token={Uri.EscapeDataString(token)}"),
            HttpContext.RequestAborted);
        if (result.CodeInvalid)
        {
            ModelState.AddModelError("Input.Code", "That enrollment code is not valid or has expired. Ask the clinic for a new one.");
            return Page();
        }

        foreach (var error in result.Errors)
        {
            ModelState.AddModelError("", error);
        }

        Registered = result.Succeeded;
        return Page();
    }
}
