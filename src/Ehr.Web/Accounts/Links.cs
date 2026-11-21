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

    // For the portal: only a page on this site, and only the page, without its query, as the place to go after signing
    // in. A query can ask a page for what its reader never chose: "Download my record" for its file, sensitive items
    // shown, on the download's pages or on Visit summaries (the expansion plan's step 13b). A link from anywhere lands
    // at sign-in with its query in ReturnUrl, so whoever wrote the link would choose; on the page itself, the person
    // chooses, after reading what the page says.
    public static string LocalPage(IUrlHelper url, string? returnUrl, string fallback)
    {
        var local = Local(url, returnUrl, fallback);
        var query = local.IndexOfAny(['?', '#']);
        return query < 0 ? local : local[..query];
    }
}
