namespace Ehr.Domain.Accounts;

// Ehr:Links: the addresses people reach each site at, for the links the app sends out: a patient's confirmation or
// password-reset email, an invitation, a new staff member's setup link. A link is always built from here and never
// from the request: a form posted to the cloud site is handled by the on-prem app, whose own address the person can't
// reach, and a request's Host header is whatever its sender wrote, so a link built from it could send someone their
// token on a link to another site. Both are required outside Development: the app refuses to start without them.
public sealed class SiteAddresses
{
    public const string Section = "Ehr:Links";

    // Development only, when nothing is set: the address in src/Ehr.Web/Properties/launchSettings.json.
    public const string DevelopmentAddress = "http://localhost:5080";

    // Where clinicians reach the clinical app, such as https://ehr-remote.example.com:9443.
    public string? ClinicalBaseUrl { get; set; }

    // Where patients reach the portal, such as https://portal.example.com:9443.
    public string? PortalBaseUrl { get; set; }

    // The addresses to use, checked: each an absolute http(s) address. Outside Development a missing one is an error
    // that says which setting to set; in Development it becomes the local address.
    public static SiteAddresses Checked(SiteAddresses? configured, bool development) => new()
    {
        ClinicalBaseUrl = Checked(configured?.ClinicalBaseUrl, nameof(ClinicalBaseUrl), "the clinical app", development),
        PortalBaseUrl = Checked(configured?.PortalBaseUrl, nameof(PortalBaseUrl), "the patient portal", development),
    };

    static string Checked(string? value, string name, string site, bool development)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return development
                ? DevelopmentAddress
                : throw new InvalidOperationException(
                    $"Set {Section}:{name} to the address people reach {site} at, such as https://portal.example.com. "
                    + "The app won't start without it: the links it emails are built from this address and never from a request.");
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var address) && address.Scheme is "https" or "http"
            ? value.TrimEnd('/')
            : throw new InvalidOperationException($"{Section}:{name} must be an absolute address starting with https://, not \"{value}\".");
    }
}
