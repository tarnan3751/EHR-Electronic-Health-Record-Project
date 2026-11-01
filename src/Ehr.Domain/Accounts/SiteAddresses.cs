namespace Ehr.Domain.Accounts;

// Ehr:Links: the addresses people reach each site at, for the links the app hands out: a patient's confirmation email,
// a new staff member's setup link. A link is built from here rather than from the request, because a form posted to
// the cloud site is handled by the on-prem app, whose own address the person can't reach. Empty means the request's
// own address, which is right for development and the tests.
public sealed class SiteAddresses
{
    public const string Section = "Ehr:Links";

    // Where clinicians reach the clinical app, such as https://ehr-remote.example.com:9443.
    public string? ClinicalBaseUrl { get; set; }

    // Where patients reach the portal, such as https://portal.example.com:9443.
    public string? PortalBaseUrl { get; set; }
}
