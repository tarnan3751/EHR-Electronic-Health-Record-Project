using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Ehr.Web.Accounts;

// Absolute links the app hands out, built from the configured address of the site (SiteAddresses) and nothing else:
// never from the request, whose Host header is its sender's to choose. The address is checked at startup (Program.cs).
public static class Links
{
    public static string Build(string? baseUrl, string path) =>
        string.IsNullOrEmpty(baseUrl)
            ? throw new InvalidOperationException($"{Ehr.Domain.Accounts.SiteAddresses.Section} has no address for this link.")
            : baseUrl.TrimEnd('/') + path;

    // Only a path on this site, never somewhere else. Private: a sign-in goes on through LocalPage, never with the query.
    static string Local(IUrlHelper url, string? returnUrl, string fallback) =>
        !string.IsNullOrEmpty(returnUrl) && url.IsLocalUrl(returnUrl) ? returnUrl : fallback;

    // Only a page on this site, and only the page, without its query, as the place to go after signing in: for both
    // sign-ins, the portal's and the staff's, at every step that ends one. A query can ask a page for what its reader
    // never chose: "Download my record" for its file, sensitive items shown on a chart, a note, a list's history, the
    // download's pages or Visit summaries (the expansion plan's step 13b). A link from anywhere lands at sign-in with
    // its query in ReturnUrl, so whoever wrote the link would choose; on the page itself, the person chooses, after
    // reading what the page says. The whole query goes, not the parameters known today, so the next one is covered too,
    // but for the parameters a caller names in `keep`, in that order: the staff sign-in keeps a patient search's
    // criteria, so a search left when a session ended comes back after signing in (SearchQuery.Parameters). Never a
    // handler or a request for sensitive items.
    public static string LocalPage(IUrlHelper url, string? returnUrl, string fallback, params IReadOnlyList<string> keep)
    {
        var local = Local(url, returnUrl, fallback);
        var fragment = local.IndexOf('#');
        if (fragment >= 0)
        {
            local = local[..fragment];
        }

        var start = local.IndexOf('?');
        if (start < 0)
        {
            return local;
        }

        var query = QueryHelpers.ParseQuery(local[start..]);
        var kept = keep.Where(name => query.ContainsKey(name))
            .SelectMany(name => query[name].Select(value => KeyValuePair.Create(name, value)));
        return QueryHelpers.AddQueryString(local[..start], kept);
    }
}
