using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Account;

// Where a signed-in staff account lands when a policy refuses it, such as a clinician at the staff admin page.
public class DeniedModel : PageModel
{
    public void OnGet()
    {
    }
}
