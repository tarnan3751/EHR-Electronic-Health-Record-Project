using System.Net;
using Ehr.Data.Accounts;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The first layer of authorization: the policies attached to the areas (Program.cs, EhrPolicies). Who gets sent to
// sign in, who is refused, and what stays anonymous.
public class AuthorizationTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/", "/sign-in")]
    [InlineData("/patients", "/sign-in")]
    [InlineData("/admin/staff", "/sign-in")]
    [InlineData("/portal", "/portal/sign-in")]
    public async Task Anonymous_requests_are_sent_to_the_right_sign_in(string path, string signIn)
    {
        using var response = await Client(apps.OnPrem, new CookieContainer(), followRedirects: false).GetAsync(path, Cancel);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(signIn, new Uri(Base, response.Headers.Location!).AbsolutePath);
    }

    [Theory]
    [InlineData("/_content/Ehr.Design/css/ehr.css")]
    [InlineData("/api/sync/quick-texts?since=0")]
    [InlineData("/sign-in")]
    [InlineData("/portal/sign-in")]
    [InlineData("/portal/register")]
    public async Task Static_files_the_sync_api_and_the_sign_in_pages_are_anonymous(string path)
    {
        using var response = await Client(apps.OnPrem, new CookieContainer(), followRedirects: false).GetAsync(path, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_clinician_is_refused_at_the_staff_admin_page()
    {
        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());

        using var response = await Client(apps.OnPrem, session.Jar, followRedirects: false).GetAsync("/admin/staff", Cancel);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/denied", new Uri(Base, response.Headers.Location!).AbsolutePath);
    }

    [Fact]
    public async Task An_admin_is_refused_at_patients_and_welcome_at_quick_text()
    {
        var admin = await apps.CreateStaffAsync("An Admin", StaffRoles.Admin);
        var session = await SignInAsync(apps.OnPrem, admin);
        using var client = Client(apps.OnPrem, session.Jar, followRedirects: false);

        using var patients = await client.GetAsync("/patients", Cancel);
        using var quickText = await client.GetAsync("/", Cancel);
        using var staff = await client.GetAsync("/admin/staff", Cancel);

        Assert.Equal("/denied", new Uri(Base, patients.Headers.Location!).AbsolutePath);
        Assert.Equal(HttpStatusCode.OK, quickText.StatusCode);
        Assert.Equal(HttpStatusCode.OK, staff.StatusCode);
    }

    [Fact]
    public async Task A_staff_cookie_is_not_a_patient_cookie()
    {
        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());

        using var response = await Client(apps.OnPrem, session.Jar, followRedirects: false).GetAsync("/portal", Cancel);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/portal/sign-in", new Uri(Base, response.Headers.Location!).AbsolutePath);
    }

    [Fact]
    public async Task The_same_rules_hold_on_the_cloud_app()
    {
        using var anonymous = await Client(apps.Cloud, new CookieContainer(), followRedirects: false).GetAsync("/patients", Cancel);
        Assert.Equal("/sign-in", new Uri(Base, anonymous.Headers.Location!).AbsolutePath);

        var session = await SignInAsync(apps.Cloud, await apps.ClinicianAsync());
        using var signedIn = await Client(apps.Cloud, session.Jar, followRedirects: false).GetAsync("/", Cancel);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
    }
}
