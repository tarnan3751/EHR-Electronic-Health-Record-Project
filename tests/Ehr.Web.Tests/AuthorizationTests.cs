using System.Net;
using Ehr.Data.Accounts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The first layer of authorization: the policies attached to the areas (Program.cs, EhrPolicies). Who gets sent to
// sign in, who is refused, and what stays anonymous.
public class AuthorizationTests(WebApps apps) : IClassFixture<WebApps>
{
    [Theory]
    [InlineData("/", "/sign-in")]
    [InlineData("/patients", "/sign-in")]
    [InlineData("/patients/00000000-0000-0000-0000-000000000001/notes/drafts/00000000-0000-0000-0000-000000000002", "/sign-in")]
    [InlineData("/patients/00000000-0000-0000-0000-000000000001/notes/drafts/00000000-0000-0000-0000-000000000002/confirm", "/sign-in")]
    [InlineData("/patients/00000000-0000-0000-0000-000000000001/notes/00000000-0000-0000-0000-000000000002", "/sign-in")]
    [InlineData("/admin/staff", "/sign-in")]
    [InlineData("/session", "/sign-in")]
    [InlineData("/admin/emergency-access/00000000-0000-0000-0000-000000000001", "/sign-in")]
    [InlineData("/admin/staff/00000000-0000-0000-0000-000000000001/desktop", "/sign-in")]
    [InlineData("/passkeys", "/sign-in")]
    [InlineData("/portal", "/portal/sign-in")]
    [InlineData("/portal/session", "/portal/sign-in")]
    public async Task Anonymous_requests_are_sent_to_the_right_sign_in(string path, string signIn)
    {
        using var response = await Client(apps.OnPrem, new CookieContainer(), followRedirects: false).GetAsync(path, Cancel);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(signIn, new Uri(Base, response.Headers.Location!).AbsolutePath);
    }

    // The portal's own address opened bare, as the local stack's https://portal.example.com:9443/ is: / there goes to the
    // portal, signed in or not, which signs a patient in as it does at /portal. The staff sites' / is the clinic's home,
    // as before, which sends anyone not signed in to the staff sign-in. (With both addresses the same, as in the other
    // tests and in Development, / is the clinic's home everywhere: the first case above.)
    [Fact]
    public async Task The_portals_own_address_sends_its_root_to_the_portal()
    {
        var portal = new Uri("https://portal.example.test:9443");
        using var cloud = apps.Cloud.WithWebHostBuilder(builder => builder.UseSetting("Ehr:Links:PortalBaseUrl", portal.ToString()));
        HttpClient At(Uri site, CookieContainer jar, bool followRedirects = false) => followRedirects
            ? cloud.CreateDefaultClient(site, new RedirectHandler(), new CookieContainerHandler(jar))
            : cloud.CreateDefaultClient(site, new CookieContainerHandler(jar));

        using (var root = await At(portal, new CookieContainer()).GetAsync("/", Cancel))
        {
            Assert.Equal(HttpStatusCode.Redirect, root.StatusCode);
            Assert.Equal("/portal", root.Headers.Location!.OriginalString);
        }

        using (var followed = await At(portal, new CookieContainer(), followRedirects: true).GetAsync("/", Cancel))
        {
            Assert.Equal("/portal/sign-in", PathOf(followed));
        }

        foreach (var staff in new[] { Base, new Uri(WebApps.OffSiteOrigin) })
        {
            using var root = await At(staff, new CookieContainer()).GetAsync("/", Cancel);
            Assert.Equal(HttpStatusCode.Redirect, root.StatusCode);
            Assert.Equal("/sign-in", new Uri(staff, root.Headers.Location!).AbsolutePath);
        }

        // Signed in to the portal at its own address, / is the portal's home, where it was the staff sign-in.
        var (email, password) = await apps.CreatePatientAccountAsync(await apps.InsertPatientAsync("Synthetic", "Rootpath"));
        var client = At(portal, new CookieContainer(), followRedirects: true);
        using (var signedIn = await PostFormAsync(client, "/portal/sign-in", "/portal/sign-in", ("Input.Email", email), ("Input.Password", password)))
        {
            Assert.Equal("/portal", PathOf(signedIn));
        }

        using var home = await client.GetAsync("/", Cancel);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Equal("/portal", PathOf(home));
    }

    [Theory]
    [InlineData("/_content/Ehr.Design/css/ehr.css")]
    [InlineData("/sign-in")]
    [InlineData("/portal/sign-in")]
    [InlineData("/portal/register")]
    public async Task Static_files_and_the_sign_in_pages_are_anonymous(string path)
    {
        using var response = await Client(apps.OnPrem, new CookieContainer(), followRedirects: false).GetAsync(path, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The desktop app is not a browser, and the pages' scripts read the status: a refused API request gets a 401, never
    // a redirect to the sign-in page, whether the API takes the desktop token or the staff cookie (dictation, and the
    // FHIR API, which isn't under /api).
    [Theory]
    [InlineData("GET", "/api/sync/quick-texts?since=0")]
    [InlineData("POST", "/api/desktop/sign-out")]
    [InlineData("POST", "/api/dictation/sessions")]
    [InlineData("GET", "/fhir/metadata")]
    [InlineData("GET", "/fhir/Patient/00000000-0000-0000-0000-000000000001")]
    public async Task The_apis_answer_an_anonymous_request_with_401(string method, string path)
    {
        using var client = Client(apps.OnPrem, new CookieContainer(), followRedirects: false);
        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task A_clinician_is_refused_at_the_staff_admin_page()
    {
        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());

        // The staff page, an emergency access's review (step 18), and a staff account's desktop sign-ins (step 20).
        foreach (var path in new[]
        {
            "/admin/staff", "/admin/emergency-access/00000000-0000-0000-0000-000000000001",
            "/admin/staff/00000000-0000-0000-0000-000000000001/desktop",
        })
        {
            using var response = await Client(apps.OnPrem, session.Jar, followRedirects: false).GetAsync(path, Cancel);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/denied", new Uri(Base, response.Headers.Location!).AbsolutePath);
        }
    }

    [Fact]
    public async Task An_admin_is_refused_at_patients_and_welcome_at_quick_text()
    {
        var admin = await apps.CreateStaffAsync("An Admin", StaffRoles.Admin);
        var session = await SignInAsync(apps.OnPrem, admin);
        using var client = Client(apps.OnPrem, session.Jar, followRedirects: false);

        using var patients = await client.GetAsync("/patients", Cancel);
        using var quickText = await client.GetAsync(QuickTexts, Cancel);
        using var staff = await client.GetAsync("/admin/staff", Cancel);
        // Their own passkeys, as any staff account's (step 19).
        using var passkeys = await client.GetAsync("/passkeys", Cancel);

        Assert.Equal("/denied", new Uri(Base, patients.Headers.Location!).AbsolutePath);
        Assert.Equal(HttpStatusCode.OK, quickText.StatusCode);
        Assert.Equal(HttpStatusCode.OK, staff.StatusCode);
        Assert.Equal(HttpStatusCode.OK, passkeys.StatusCode);
    }

    [Fact]
    public async Task A_staff_cookie_is_not_a_patient_cookie()
    {
        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());

        // The portal's pages, its session page among them (step 18).
        foreach (var path in new[] { "/portal", "/portal/session" })
        {
            using var response = await Client(apps.OnPrem, session.Jar, followRedirects: false).GetAsync(path, Cancel);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/portal/sign-in", new Uri(Base, response.Headers.Location!).AbsolutePath);
        }
    }

    [Fact]
    public async Task The_same_rules_hold_on_the_cloud_app()
    {
        using var anonymous = await Client(apps.Cloud, new CookieContainer(), followRedirects: false).GetAsync("/patients", Cancel);
        Assert.Equal("/sign-in", new Uri(Base, anonymous.Headers.Location!).AbsolutePath);

        var session = await SignInAsync(apps.Cloud, await apps.ClinicianAsync());
        using var signedIn = await Client(apps.Cloud, session.Jar, followRedirects: false).GetAsync(QuickTexts, Cancel);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
    }
}
