using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Account;

// What the clinic shows at an address with nothing there, in place of an empty 404 (ErrorPages): the same page whether
// nothing exists there or it isn't this account's to see, so it says neither. Anonymous, like the other account
// pages, so it can be shown to anyone; a page that needs a sign-in sends someone not signed in to sign in before this.
public class NotFoundModel : PageModel
{
    // A 404 however it's reached, opened at its own address included.
    public void OnGet() => Response.StatusCode = StatusCodes.Status404NotFound;
}
