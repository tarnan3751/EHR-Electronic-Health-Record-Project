using Microsoft.AspNetCore.Mvc;

namespace Ehr.Web.Accounts;

// Absolute links the app hands out, built from the configured address of the site (SiteAddresses) and nothing else:
// never from the request, whose Host header is its sender's to choose. The address is checked at startup (Program.cs).
public static class Links
{
    public static string Build(string? baseUrl, string path) =>
        string.IsNullOrEmpty(baseUrl)
            ? throw new InvalidOperationException($"{Ehr.Domain.Accounts.SiteAddresses.Section} has no address for this link.")
            : baseUrl.TrimEnd('/') + path;

    // Only a path on this site, never somewhere else, as the place to go after signing in.
    public static string Local(IUrlHelper url, string? returnUrl, string fallback) =>
        !string.IsNullOrEmpty(returnUrl) && url.IsLocalUrl(returnUrl) ? returnUrl : fallback;
}
