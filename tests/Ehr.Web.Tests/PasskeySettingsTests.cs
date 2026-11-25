using Ehr.Domain.Accounts;
using Xunit;

namespace Ehr.Web.Tests;

// Ehr:Passkeys (step 19), checked at startup: off unless both are set, outside Development; a bare domain; and each
// origin a staff site's https address on that domain or under it, written as a browser writes an origin.
public class PasskeySettingsTests
{
    [Fact]
    public void Neither_set_is_off_outside_Development_and_the_local_address_in_it()
    {
        Assert.False(PasskeySettings.Checked(null, development: false).On);
        Assert.False(PasskeySettings.Checked(new PasskeySettings(), development: false).On);

        var development = PasskeySettings.Checked(null, development: true);
        Assert.Equal("localhost", development.Domain);
        Assert.Equal([SiteAddresses.DevelopmentAddress], development.Origins);
    }

    [Fact]
    public void The_stacks_settings_are_kept_as_browsers_write_an_origin()
    {
        var passkeys = PasskeySettings.Checked(
            new PasskeySettings { Domain = "example.com", Origins = ["https://EHR.example.com:8443", "https://ehr-remote.example.com:9443/", "https://ehr.example.com:443"] },
            development: false);

        Assert.True(passkeys.On);
        Assert.Equal(["https://ehr.example.com:8443", "https://ehr-remote.example.com:9443", "https://ehr.example.com"], passkeys.Origins);
        Assert.True(passkeys.Allows("https://ehr-remote.example.com:9443"));
        Assert.False(passkeys.Allows("https://portal.example.com:9443"));
        Assert.False(passkeys.Allows("https://ehr.example.com:9443"));
        Assert.False(passkeys.Allows("http://ehr.example.com:8443"));
    }

    [Theory]
    [InlineData("example.com", null)]
    [InlineData(null, "https://ehr.example.com")]
    [InlineData("https://example.com", "https://ehr.example.com")]
    [InlineData("example.com:443", "https://ehr.example.com")]
    [InlineData("Example.com", "https://ehr.example.com")]
    [InlineData("example.com", "https://ehr.example.org")]
    [InlineData("example.com", "https://notexample.com")]
    [InlineData("example.com", "http://ehr.example.com")]
    [InlineData("example.com", "https://ehr.example.com/sign-in")]
    [InlineData("example.com", "https://ehr.example.com?x=1")]
    [InlineData("example.com", "https://user@ehr.example.com")]
    [InlineData("example.com", " https://ehr.example.com")]
    public void A_wrong_setting_stops_the_app_at_startup(string? domain, string? origin)
    {
        var configured = new PasskeySettings { Domain = domain, Origins = origin is null ? [] : [origin] };

        Assert.Throws<InvalidOperationException>(() => PasskeySettings.Checked(configured, development: false));
    }

    [Fact]
    public void Development_takes_http_for_the_local_address()
    {
        var passkeys = PasskeySettings.Checked(new PasskeySettings { Domain = "localhost", Origins = ["http://localhost:5080"] }, development: true);

        Assert.Equal(["http://localhost:5080"], passkeys.Origins);
    }
}
