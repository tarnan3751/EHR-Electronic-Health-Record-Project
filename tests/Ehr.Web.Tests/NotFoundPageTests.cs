using System.Net;
using Ehr.Data.Accounts;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// An address with nothing there (ErrorPages, Account/NotFound in each area): the site's own page, in its layout, with
// the 404 kept and a way back, on-site and off-site; the same page for a record that isn't the account's as for one
// that doesn't exist, opened or posted to; and the APIs and htmx requests left with their bare statuses, as before.
public class NotFoundPageTests(WebApps apps) : IClassFixture<WebApps>
{
    WebApplicationFactory<Program> App(string site) => site switch
    {
        "on-site" => apps.OnPrem,
        "off-site" => apps.Cloud,
        _ => apps.CloudCutOff,
    };

    [Theory]
    [InlineData("on-site")]
    [InlineData("off-site")]
    [InlineData("cut off")]
    public async Task The_clinic_shows_its_not_found_page_in_its_layout(string site)
    {
        var clinician = await SignInAsync(App(site), await apps.ClinicianAsync(), via: site == "cut off" ? apps.Cloud : null);
        var admin = await SignInAsync(App(site), await apps.CreateStaffAsync("An Admin Lost", StaffRoles.Admin), via: site == "cut off" ? apps.Cloud : null);

        using var lost = await clinician.Client.GetAsync("/this-page-does-not-exist", Cancel);
        using var adminLost = await admin.Client.GetAsync("/admin/desktop-sign-ins", Cancel);

        foreach (var response in new[] { lost, adminLost })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync(Cancel);
            Assert.Contains("<title>Page not found</title>", html);
            Assert.Contains("There's nothing to show at this address.", Words(html));
            // The clinic's app bar, signed in, and its session warning, as on every signed-in page.
            Assert.Contains("sc-appbar--clinician", html);
            Assert.Contains("data-session-warning", html);
        }

        // Back to the clinician's patients; an admin, who has none, to the start, which is the staff page.
        Assert.Contains("<a href=\"/patients\">Back to patients</a>", await lost.Content.ReadAsStringAsync(Cancel));
        Assert.Contains("<a href=\"/\">Back to home</a>", await adminLost.Content.ReadAsStringAsync(Cancel));
    }

    [Theory]
    [InlineData("on-site")]
    [InlineData("off-site")]
    [InlineData("cut off")]
    public async Task The_portal_shows_its_not_found_page_in_its_layout(string site)
    {
        var (_, jar) = await PortalResultsTests.SignInPatientAsync(apps, site == "cut off" ? apps.Cloud : App(site), await apps.InsertPatientAsync("Synthetic", "Lost"));

        using var lost = await Client(App(site), jar).GetAsync("/portal/does-not-exist", Cancel);

        Assert.Equal(HttpStatusCode.NotFound, lost.StatusCode);
        var html = await lost.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("<title>Page not found</title>", html);
        Assert.Contains("There's nothing to show at this address.", Words(html));
        Assert.Contains("sc-appbar--portal", html);
        Assert.Contains("<a href=\"/portal\">Back to home</a>", html);
    }

    // Another patient's message and a message that was never written answer the same, opened or replied to: the page
    // says neither which.
    [Fact]
    public async Task Another_patients_message_looks_like_one_that_does_not_exist()
    {
        var (_, writerPatient, _, writer) = await MessageTests.CareAsync(apps, "Dr. Not Found Thread");
        var thread = await MessageTests.StartAsync(writer.Client, "Synthetic: a private question", "Synthetic: only mine.");
        var (other, _) = await PortalResultsTests.SignInPatientAsync(apps, apps.OnPrem, await apps.InsertPatientAsync("Synthetic", "Other"));

        var nobody = Guid.CreateVersion7();
        using var someoneElses = await other.GetAsync($"/portal/messages/{thread}", Cancel);
        using var nobodys = await other.GetAsync($"/portal/messages/{nobody}", Cancel);
        using var replyToSomeoneElses = await PostFormAsync(other, $"/portal/messages/{thread}", "/portal/messages", ("Body", "Synthetic: a reply."));
        using var replyToNobodys = await PostFormAsync(other, $"/portal/messages/{nobody}", "/portal/messages", ("Body", "Synthetic: a reply."));

        var shown = Words(await someoneElses.Content.ReadAsStringAsync(Cancel));
        Assert.Contains("Page not found", shown);
        Assert.DoesNotContain("private question", shown);
        foreach (var response in new[] { someoneElses, nobodys, replyToSomeoneElses, replyToNobodys })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(shown, Words(await response.Content.ReadAsStringAsync(Cancel)));
        }

        Assert.Equal(["Synthetic: only mine."], await MessageTests.RowsAsync(apps, writerPatient, "body"));
    }

    // Nobody signed in: an address that needs a sign-in still sends them there, and the page itself, at its own address,
    // offers the sign-in.
    [Fact]
    public async Task Signed_out_the_not_found_page_offers_the_sign_in()
    {
        using var client = Client(apps.OnPrem, new CookieContainer(), followRedirects: false);

        using var nowhere = await client.GetAsync("/this-page-does-not-exist", Cancel);
        Assert.Equal(HttpStatusCode.Redirect, nowhere.StatusCode);
        Assert.Equal("/sign-in", new Uri(Base, nowhere.Headers.Location!).AbsolutePath);

        foreach (var (page, signIn) in new[] { ("/not-found", "/sign-in"), ("/portal/not-found", "/portal/sign-in") })
        {
            using var response = await client.GetAsync(page, Cancel);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync(Cancel);
            Assert.Contains($"<a href=\"{signIn}\">Sign in</a>", html);
            Assert.DoesNotContain("sc-appbar", html);
            Assert.DoesNotContain("data-session-warning", html);
        }
    }

    // The FHIR API keeps its OperationOutcome, the desktop's API its bare 401, and htmx its bare 404.
    [Fact]
    public async Task The_apis_and_htmx_keep_their_bare_answers()
    {
        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());

        using var fhir = await session.Client.GetAsync("/fhir/NoSuchType", Cancel);
        Assert.NotEqual("text/html", fhir.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("Page not found", await fhir.Content.ReadAsStringAsync(Cancel));

        using var api = await session.Client.GetAsync("/api/nothing-here", Cancel);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Equal("", await api.Content.ReadAsStringAsync(Cancel));

        using var htmx = new HttpRequestMessage(HttpMethod.Get, "/this-page-does-not-exist");
        htmx.Headers.Add("HX-Request", "true");
        using var fragment = await session.Client.SendAsync(htmx, Cancel);
        Assert.Equal(HttpStatusCode.NotFound, fragment.StatusCode);
        Assert.Equal("", await fragment.Content.ReadAsStringAsync(Cancel));
    }
}
