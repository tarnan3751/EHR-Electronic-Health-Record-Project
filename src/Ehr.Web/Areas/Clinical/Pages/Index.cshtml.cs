using Ehr.Data.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages;

// The clinic's home: a clinician's own patients first, an admin's staff page. Anyone not signed in is sent to sign in
// by the area's policy before this runs.
public class IndexModel : PageModel
{
    public IActionResult OnGet() =>
        User.IsInRole(StaffRoles.Clinician) ? RedirectToPage("/Patients/Index") : RedirectToPage("/Admin/Staff");
}
