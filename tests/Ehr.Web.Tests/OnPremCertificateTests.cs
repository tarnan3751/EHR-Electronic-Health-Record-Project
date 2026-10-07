using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ehr.Web.Forwarding;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Ehr.Web.Tests;

// Which certificates the cloud app accepts from the on-prem app (OnPremApp.IsTrusted).
public class OnPremCertificateTests
{
    static readonly X509Certificate2 Shared = SelfSigned();
    static readonly X509Certificate2 Other = SelfSigned();

    [Fact]
    public void A_certificate_the_system_trusts_is_accepted() =>
        Assert.True(OnPremApp.IsTrusted(Other, SslPolicyErrors.None, Shared));

    [Fact]
    public void The_shared_certificate_is_accepted_without_a_trusted_root() =>
        Assert.True(OnPremApp.IsTrusted(Shared, SslPolicyErrors.RemoteCertificateChainErrors, Shared));

    [Fact]
    public void Any_other_untrusted_certificate_is_refused() =>
        Assert.False(OnPremApp.IsTrusted(Other, SslPolicyErrors.RemoteCertificateChainErrors, Shared));

    [Fact]
    public void The_shared_certificate_is_refused_for_the_wrong_host_name() =>
        Assert.False(OnPremApp.IsTrusted(
            Shared, SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch, Shared));

    [Fact]
    public void Without_a_shared_certificate_only_trusted_ones_are_accepted() =>
        Assert.False(OnPremApp.IsTrusted(Shared, SslPolicyErrors.RemoteCertificateChainErrors, shared: null));

    [Fact]
    public void A_missing_certificate_is_refused() =>
        Assert.False(OnPremApp.IsTrusted(null, SslPolicyErrors.RemoteCertificateNotAvailable, Shared));

    // Certificate files hold the certificate only (mkcert writes its key to a separate file).
    [Fact]
    public void The_shared_certificate_is_read_from_a_file_without_its_key()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, Shared.ExportCertificatePem());
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Ehr:OnPrem:Url"] = "https://onprem.example.com:8443",
                    ["Ehr:OnPrem:Certificate"] = path,
                })
                .Build();

            Assert.Equal("https://onprem.example.com:8443", OnPremApp.FromConfiguration(configuration).Url);
        }
        finally
        {
            File.Delete(path);
        }
    }

    static X509Certificate2 SelfSigned()
    {
        using var key = ECDsa.Create();
        var request = new CertificateRequest("CN=example.com", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
