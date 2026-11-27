using System.Buffers.Text;
using System.Security.Claims;
using System.Text.Json;
using Ehr.Data.Accounts;
using Ehr.Domain.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Passkeys;

// A staff member's own passkeys (the expansion plan's step 19, UC 11): the list, adding one, removing one. Under the
// Clinical area's Staff policy, as every page outside its Account folder is, and only ever the signed-in account's own.
// Adding takes two posts: the name and a current authenticator code, answered with JSON for passkeys.js to make the
// passkey with (StaffPasskeys), then the passkey the browser made. Off-site both are forwarded to the clinic's server,
// as every save is; cut off from it, the forms give way to a sentence. Not there at all without Ehr:Passkeys.
public class IndexModel(StaffPasskeys passkeys, UserManager<StaffUser> users, PasskeySettings settings) : PageModel
{
    public IReadOnlyList<UserPasskeyInfo> Passkeys { get; private set; } = [];

    [TempData]
    public string? Message { get; set; }

    public string? Problem { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!settings.On)
        {
            return NotFound();
        }

        Passkeys = await passkeys.ListAsync(await CurrentAsync());
        return Page();
    }

    // The name and the code checked, then the options for the browser and the state to send back; or the field that
    // is wrong and why.
    public async Task<IActionResult> OnPostOptionsAsync(string? name, string? code)
    {
        if (!settings.On)
        {
            return NotFound();
        }

        var ceremony = await passkeys.BeginAddAsync(HttpContext, await CurrentAsync(), name, code, HttpContext.RequestAborted);
        return ceremony.Problem is { } problem
            ? new JsonResult(new { field = ceremony.Field, problem }) { StatusCode = StatusCodes.Status422UnprocessableEntity }
            : new JsonResult(new { options = JsonSerializer.Deserialize<JsonElement>(ceremony.Options!), state = ceremony.State });
    }

    public async Task<IActionResult> OnPostAddAsync(string? credential, string? state)
    {
        if (!settings.On)
        {
            return NotFound();
        }

        var user = await CurrentAsync();
        if (await passkeys.FinishAddAsync(HttpContext, user, credential, state, HttpContext.RequestAborted) is { } problem)
        {
            Problem = problem;
            Passkeys = await passkeys.ListAsync(user);
            return Page();
        }

        Message = "Passkey added.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(string? id)
    {
        if (!settings.On)
        {
            return NotFound();
        }

        var user = await CurrentAsync();
        if (id is null || !Base64Url.IsValid(id) || !await passkeys.RemoveAsync(user, Base64Url.DecodeFromChars(id), HttpContext.RequestAborted))
        {
            Problem = "Not removed. Passkey not found.";
            Passkeys = await passkeys.ListAsync(user);
            return Page();
        }

        Message = "Passkey removed.";
        return RedirectToPage();
    }

    public static string IdOf(UserPasskeyInfo passkey) => Base64Url.EncodeToString(passkey.CredentialId);

    // A notice's state, its first sentence and the full stop, and the rest; a notice of one sentence is its state alone.
    public static string State(string notice) => notice.IndexOf(". ", StringComparison.Ordinal) is var stop and >= 0 ? notice[..(stop + 1)] : notice;

    public static string Rest(string notice) => notice.IndexOf(". ", StringComparison.Ordinal) is var stop and >= 0 ? notice[(stop + 1)..] : "";

    async Task<StaffUser> CurrentAsync() => (await users.FindByIdAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!))!;
}
