using Ehr.Domain.Accounts;
using Microsoft.AspNetCore.Hosting;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The expansion plan's step 6c. Every link the app emails (password reset, confirmation, invitation, staff set-up) is
// built from the configured address of the site, never from the request: a request's Host header is its sender's to
// write, and a link built from it would send someone their token on a link to another site. Outside Development the
// app refuses to start without the addresses, and without the key enrollment codes are hashed with, saying what to set.
public class EmailedLinkTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_reset_link_is_built_from_the_configured_address_whatever_host_the_request_names()
    {
        var patient = await apps.InsertPatientAsync("Synthetic", "Resetter");
        var (email, _) = await apps.CreatePatientAccountAsync(patient);
        var client = CreateClient(apps.OnPrem);

        using var page = new HttpRequestMessage(HttpMethod.Get, "/portal/forgot");
        page.Headers.Host = "attacker.example";
        using var form = await client.SendAsync(page, Cancel);
        using var post = new HttpRequestMessage(HttpMethod.Post, "/portal/forgot");
        post.Headers.Host = "attacker.example";
        post.Content = Form(("__RequestVerificationToken", Token(await form.Content.ReadAsStringAsync(Cancel))), ("Email", email));
        using var asked = await client.SendAsync(post, Cancel);
        Assert.True(asked.IsSuccessStatusCode);

        var sent = apps.Emails.Sent.Last(message => string.Equals(message.To, email, StringComparison.OrdinalIgnoreCase));
        var link = apps.Emails.Actions[sent.Body].Link;
        Assert.StartsWith(Base + "portal/reset?", link);
        Assert.DoesNotContain("attacker.example", link + sent.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Ehr:Links:PortalBaseUrl", "Set Ehr:Links:PortalBaseUrl to the address people reach the patient portal at")]
    [InlineData("Ehr:Links:ClinicalBaseUrl", "Set Ehr:Links:ClinicalBaseUrl to the address people reach the clinical app at")]
    [InlineData(CodeHasher.Setting, "Set Ehr:Enrollment:Key to a secret of at least 32 characters")]
    public void Outside_development_the_app_does_not_start_without_its_addresses_or_its_enrollment_key(string setting, string message)
    {
        using var app = apps.OnPremProduction.WithWebHostBuilder(builder => builder.UseSetting(setting, ""));

        var refused = Record.Exception(() => app.CreateClient());

        Assert.NotNull(refused);
        Assert.Contains(message, refused.ToString());
        Assert.Contains("won't start without it", refused.ToString());
    }

    [Fact]
    public void An_address_that_is_not_one_is_refused_at_startup_and_development_has_a_local_default()
    {
        using var app = apps.OnPremProduction.WithWebHostBuilder(builder => builder.UseSetting("Ehr:Links:PortalBaseUrl", "portal.example.com"));
        Assert.Contains("must be an absolute address starting with https://", Record.Exception(() => app.CreateClient())!.ToString());

        var development = SiteAddresses.Checked(new SiteAddresses(), development: true);
        Assert.Equal(SiteAddresses.DevelopmentAddress, development.PortalBaseUrl);
        Assert.Equal(SiteAddresses.DevelopmentAddress, development.ClinicalBaseUrl);
        Assert.Throws<InvalidOperationException>(() => SiteAddresses.Checked(new SiteAddresses(), development: false));
    }
}
