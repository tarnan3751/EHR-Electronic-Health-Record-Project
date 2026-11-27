namespace Ehr.Web;

// The frame of the few pages written without Razor, by middleware that answers before any page runs: a failure
// (ErrorPages), too many attempts (AccountRateLimits) and the cloud app's "Not saved" (CloudForwardingMiddleware).
// The same head as Pages/Shared/_Layout.cshtml, less its scripts, and the signed-out pages' card, with the mark at its
// top. No inline script or style, like every page, so a strict CSP can be added later.
public static class StandalonePage
{
    // After the page's own <title>, inside <head>.
    public const string Head =
        """
        <meta name="theme-color" content="#0e1b2c" media="(prefers-color-scheme: light)">
            <meta name="theme-color" content="#091320" media="(prefers-color-scheme: dark)">
            <link rel="icon" href="/_content/Ehr.Design/img/favicon.svg" type="image/svg+xml">
            <link rel="icon" href="/_content/Ehr.Design/img/favicon-32.png" type="image/png" sizes="32x32">
            <link rel="apple-touch-icon" href="/_content/Ehr.Design/img/apple-touch-icon.png">
            <link rel="stylesheet" href="/_content/Ehr.Design/css/tokens.css">
            <link rel="stylesheet" href="/_content/Ehr.Design/css/ehr.css">
        """;

    // The opening of <body>, up to the page's own content inside <main>.
    public const string Open =
        """
        <body class="sc-app sc-app--clinician">
            <main class="sc-page sc-page--form">
                <div class="sc-card">
                    <p class="sc-brandmark">
                        <img class="sc-art--color" src="/_content/Ehr.Design/img/swiftchart-mark.svg" alt="SwiftChart" width="56" height="48">
                        <img class="sc-art--reversed" src="/_content/Ehr.Design/img/swiftchart-mark-reversed.svg" alt="SwiftChart" width="56" height="48">
                    </p>
        """;

    public const string Close =
        """
                </div>
            </main>
        </body>
        """;
}
