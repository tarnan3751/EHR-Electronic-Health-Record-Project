using System.ComponentModel.DataAnnotations;
using Ehr.Data.Accounts;
using Ehr.Domain.Accounts;
using Ehr.Web.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Account;

// Enrolling an authenticator app, at the first sign-in of every staff account. A code from the app proves it holds
// the key; the account is then signed in, since it has just given both factors, and sees its recovery codes once.
// Reached only with a StaffPending cookie from the password step, for an account unchanged since (StaffSignIn.HeldAsync,
// step 19.1): a password reissued in between sends it back to the password. Both the page and its code hold the
// account first, as the code page's posts do, and ask again under the hold (step 19.2): an administrator's reset or
// reissue landing first sends the enrolment back to the password, and one landing after waits for it, and then ends
// the new session at its next account check. The hold also keeps the order an authenticator reset keeps, the account
// and then its key: before, the enrolment wrote the key and then the account, and the two deadlocked.
public class EnrollModel(StaffSignIn signIn) : PageModel
{
    [BindProperty]
    [Required(ErrorMessage = "Enter the code.")]
    public string? Code { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public AuthenticatorSetup? Setup { get; private set; }

    // The key in groups of four, for typing into an app by hand.
    public string KeyGrouped => string.Join(" ", Setup!.Key.Chunk(4).Select(group => new string(group)));

    public IReadOnlyList<string>? RecoveryCodes { get; private set; }

    // After the recovery codes: the page the sign-in started from, without its query but for a search's criteria
    // (Links.LocalPage).
    public string Continue => Links.LocalPage(Url, ReturnUrl, "/", Patients.SearchQuery.Parameters);

    public async Task<IActionResult> OnGetAsync()
    {
        if (await HeldAsync() is not { } user)
        {
            return RedirectToPage("SignIn", new { ReturnUrl });
        }

        // A new key changes the account's security stamp: the pending sign-in carries the new one (step 19.1).
        Setup = await signIn.EnrollmentAsync(user, newKey: true);
        await signIn.RestampAsync(HttpContext, user);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await HeldAsync() is not { } user)
        {
            return RedirectToPage("SignIn", new { ReturnUrl });
        }

        var codes = ModelState.IsValid ? await signIn.FinishEnrollmentAsync(user, Code!, HttpContext.RequestAborted) : null;
        if (codes is null)
        {
            if (ModelState.IsValid)
            {
                ModelState.AddModelError(nameof(Code), "Wrong code.");
            }

            Setup = await signIn.EnrollmentAsync(user, newKey: false);
            await signIn.RestampAsync(HttpContext, user);
            return Page();
        }

        await signIn.CompleteAsync(HttpContext, user, HttpContext.RequestAborted);
        RecoveryCodes = codes;
        return Page();
    }

    // The account held by the password step, if its next step is this one; then held (StaffUserLock) and read again, and
    // still unchanged since the password step and still at this step (step 19.2).
    async Task<StaffUser?> HeldAsync()
    {
        if (await signIn.HeldAsync(HttpContext) is not { } user || await signIn.NextStepAsync(user) != SignInStep.EnrollAuthenticator)
        {
            return null;
        }

        await signIn.HoldAccountAsync(user, HttpContext.RequestAborted);
        return await signIn.UnchangedAsync(HttpContext, user) && await signIn.NextStepAsync(user) == SignInStep.EnrollAuthenticator ? user : null;
    }
}
