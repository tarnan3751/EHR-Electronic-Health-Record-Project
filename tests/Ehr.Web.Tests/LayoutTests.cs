
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The shared layout must disable htmx history snapshots
// so pages containing PHI are not cached in localStorage.
public class LayoutTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel =>
        TestContext.Current.CancellationToken;

    [Fact]
    public async Task Layout_disables_htmx_history_cache()
    {
        // Arrange: Sign in as an authorized clinician.
        var session = await SignInAsync(
            apps.OnPrem,
            await apps.ClinicianAsync());

        // Act: Request a page that uses the shared layout.
        var html = await session.Client.GetStringAsync(
            "/patients", Cancel);

        // Assert: Find the htmx configuration meta tag.
        var match = Regex.Match(
            html,
            """<meta\s+name="htmx-config"\s+content='([^']+)'""");

        Assert.True(
            match.Success,
            "The shared layout must contain an htmx-config meta tag.");

        // Parse the JSON configuration.
        using var config = JsonDocument.Parse(match.Groups[1].Value);

        Assert.True(
            config.RootElement.TryGetProperty(
                "historyCacheSize", out var cacheSize),
            "The htmx configuration must define historyCacheSize.");

        Assert.Equal(0, cacheSize.GetInt32());
    }

    // Step 18: a signed-in page, staff's or a patient's, carries the session's warning, what replaces it at the end and the
    // script that does both, and nothing of the session's own (the account, the time left), which the script asks for;
    // a page with nobody signed in carries none of them.
    [Fact]
    public async Task Signed_in_pages_carry_the_session_warning_and_signed_out_pages_none()
    {
        var clinician = await apps.ClinicianAsync();
        var session = await SignInAsync(apps.OnPrem, clinician);
        var staff = await session.Client.GetStringAsync("/quick-texts", Cancel);
        Assert.Contains("/_content/Ehr.Design/js/session.", staff);
        Assert.Contains("data-session-warning", staff);
        Assert.Contains("data-session-url=\"/session\"", staff);
        Assert.Contains("<template data-session-ended>", staff);
        Assert.DoesNotContain(clinician.Id.ToString(), staff, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data-session-left", staff);

        var patient = await apps.InsertPatientAsync("Synthetic", "Layoutportal");
        var (portal, _) = await PortalResultsTests.SignInPatientAsync(apps, apps.OnPrem, patient);
        var portalPage = await portal.GetStringAsync("/portal", Cancel);
        Assert.Contains("data-session-url=\"/portal/session\"", portalPage);
        Assert.Contains("To keep your health information safe", portalPage);

        foreach (var path in new[] { "/sign-in", "/portal/sign-in" })
        {
            var signedOut = await Client(apps.OnPrem, new System.Net.CookieContainer()).GetStringAsync(path, Cancel);
            Assert.DoesNotContain("/js/session.", signedOut);
            Assert.DoesNotContain("data-session-warning", signedOut);
        }
    }
}
