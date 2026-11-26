using Ehr.Domain.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages;

// How long the signed-in staff member's session has left (the expansion plan's step 18; SessionAnswer), for session.js
// on every clinic page. Without SessionTimes.CheckHeader the request is a keep-alive, and renews the session as any
// request does; with it, it is only a question, which never does. Under the Clinical area's Staff policy, like every
// page here, so an ended session is sent to sign in, which is how the page learns that it has ended. It reads nothing
// about a patient, and the cloud app answers it itself, as it does every page it can.
public class SessionModel : PageModel
{
    public Task<IActionResult> OnGetAsync() => SessionAnswer.ForAsync(HttpContext, AccountSchemes.Staff, acting: null);
}
