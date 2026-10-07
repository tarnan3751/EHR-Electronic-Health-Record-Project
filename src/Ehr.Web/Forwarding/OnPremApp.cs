using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Ehr.Web.Forwarding;

// The cloud app's route to the on-prem app, across the WAN (Tailscale in production), from Ehr:OnPrem:Url.
public sealed class OnPremApp(string url, HttpMessageInvoker client)
{
    public string Url { get; } = url;

    public HttpMessageInvoker Client { get; } = client;

    public static OnPremApp FromConfiguration(IConfiguration configuration)
    {
        var url = configuration["Ehr:OnPrem:Url"]
            ?? throw new InvalidOperationException("Set Ehr:OnPrem:Url to the on-prem app's address.");
        // The certificate only, without its key: the file the on-prem app serves, such as mkcert's.
        var shared = configuration["Ehr:OnPrem:Certificate"] is { } path
            ? X509Certificate2.CreateFromPem(File.ReadAllText(path))
            : null;

        // The settings YARP recommends for forwarding, plus a short connect timeout so an unreachable on-prem
        // server fails a save in seconds rather than minutes.
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions = { RemoteCertificateValidationCallback = (_, certificate, _, errors) => IsTrusted(certificate, errors, shared) },
        };
        return new OnPremApp(url, new HttpMessageInvoker(handler));
    }

    // Accepts the on-prem app's certificate if the system trusts it, as it will the real wildcard certificate, or if
    // it is exactly the shared certificate (Ehr:OnPrem:Certificate). Both servers serve the same wildcard certificate,
    // so a local mkcert or self-signed one works without installing a root. The host name must match either way.
    public static bool IsTrusted(X509Certificate? presented, SslPolicyErrors errors, X509Certificate2? shared) =>
        errors == SslPolicyErrors.None
        || (errors == SslPolicyErrors.RemoteCertificateChainErrors
            && presented is not null
            && shared is not null
            && presented.GetRawCertData().AsSpan().SequenceEqual(shared.RawData));
}
