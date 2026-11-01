using Microsoft.AspNetCore.Mvc;

namespace Ehr.Web.Accounts;

// Absolute links the app hands out, built from the configured address of the site (SiteAddresses) when there is one,
// since the on-prem app handles forms posted to the cloud site, and from the request itself otherwise.
public static class Links
{
    public static string Build(string? baseUrl, HttpRequest request, string path) =>
        (string.IsNullOrEmpty(baseUrl) ? $"{request.Scheme}://{request.Host}" : baseUrl.TrimEnd('/')) + path;

    // Only a path on this site, never somewhere else, as the place to go after signing in.
    public static string Local(IUrlHelper url, string? returnUrl, string fallback) =>
        !string.IsNullOrEmpty(returnUrl) && url.IsLocalUrl(returnUrl) ? returnUrl : fallback;
}
