using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Ehr.Web.Forwarding;

// The WAL position of a browser's latest save through the cloud app. Until the replica has replayed that far, that
// browser's page requests go to the on-prem app, so the person sees their own save. Protected, so it can't be forged
// to push someone's reads across the WAN, and good for five minutes, which bounds how long a stuck replica does that.
public sealed class LastSaveCookie(IDataProtectionProvider dataProtection)
{
    const string Name = "__Host-Ehr.LastSave";
    static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    readonly ITimeLimitedDataProtector protector =
        dataProtection.CreateProtector("Ehr.Web.LastSave").ToTimeLimitedDataProtector();

    public void Set(HttpContext context, string position) =>
        context.Response.Cookies.Append(Name, protector.Protect(position, Lifetime), new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = Lifetime,
        });

    // A cookie that has expired or was altered counts as none, and is removed.
    public bool TryRead(HttpContext context, [NotNullWhen(true)] out string? position)
    {
        position = null;
        if (!context.Request.Cookies.TryGetValue(Name, out var value))
        {
            return false;
        }

        try
        {
            position = protector.Unprotect(value);
            return true;
        }
        catch (CryptographicException)
        {
            Remove(context);
            return false;
        }
    }

    public void Remove(HttpContext context) =>
        context.Response.Cookies.Delete(Name, new CookieOptions { Secure = true, Path = "/" });
}
