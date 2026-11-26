namespace Ehr.Domain.Accounts;

// Ehr:Passkeys: whether staff may use a passkey in place of the authenticator code (the expansion plan's step 19), and
// for which sites. Domain is the relying party's ID, the domain a passkey is tied to: the parent domain both staff sites
// share, so one passkey works on-site and off-site (example.com in the local stack, for ehr.example.com and
// ehr-remote.example.com). Origins are the staff sites' exact addresses, scheme, host and port, and a passkey is
// accepted only from one of them: any other host under the domain, the portal's among them, could otherwise ask for one.
// Changing the domain later means adding passkeys again; the authenticator app still works. Optional: without both,
// outside Development, there are no passkeys and the sign-in is as before. Checked at startup (Program.cs).
public sealed class PasskeySettings
{
    public const string Section = "Ehr:Passkeys";

    public string? Domain { get; set; }

    public string[] Origins { get; set; } = [];

    // Passkeys aren't offered.
    public static readonly PasskeySettings Off = new();

    public bool On => Domain is not null;

    // A passkey made or used at this origin, as the browser wrote it in the ceremony's client data, is accepted.
    public bool Allows(string origin) => Origins.Contains(origin, StringComparer.Ordinal);

    // The settings to use, checked: a bare lowercase domain, and each origin an https address with nothing after the
    // host and port, on that domain or under it, written as browsers write an origin (no default port, no slash). In
    // Development with neither set, the local address (SiteAddresses.DevelopmentAddress) on localhost.
    public static PasskeySettings Checked(PasskeySettings? configured, bool development)
    {
        var domain = configured?.Domain;
        var origins = configured?.Origins ?? [];
        if (string.IsNullOrEmpty(domain) && origins.Length == 0)
        {
            return development ? new() { Domain = "localhost", Origins = [SiteAddresses.DevelopmentAddress] } : Off;
        }

        if (string.IsNullOrEmpty(domain) || origins.Length == 0)
        {
            throw new InvalidOperationException(
                $"Set both {Section}:Domain and {Section}:Origins, or neither: passkeys need the domain they are tied to and the staff sites' addresses.");
        }

        if (Uri.CheckHostName(domain) != UriHostNameType.Dns || domain != domain.Trim().ToLowerInvariant())
        {
            throw new InvalidOperationException(
                $"{Section}:Domain must be a domain name alone, in lowercase, such as example.com; not \"{domain}\".");
        }

        return new() { Domain = domain, Origins = [.. origins.Select(origin => CheckedOrigin(origin, domain, development)).Distinct()] };
    }

    static string CheckedOrigin(string value, string domain, bool development)
    {
        var plain = Uri.TryCreate(value, UriKind.Absolute, out var address)
            && (address.Scheme == Uri.UriSchemeHttps || (development && address.Scheme == Uri.UriSchemeHttp))
            && value == value.Trim() && address.UserInfo.Length == 0 && address.AbsolutePath == "/" && address.Query.Length == 0 && address.Fragment.Length == 0
            && (address.Host == domain || address.Host.EndsWith("." + domain, StringComparison.Ordinal));
        return plain
            ? address!.GetLeftPart(UriPartial.Authority)
            : throw new InvalidOperationException(
                $"Each of {Section}:Origins must be a staff site's address starting with https://, on {domain} or under it, with nothing after the host and port, such as https://ehr.{domain}:8443; not \"{value}\".");
    }
}
