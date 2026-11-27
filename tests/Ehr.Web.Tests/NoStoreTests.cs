using Ehr.Data.Accounts;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Step 18: every page served to someone signed in is "no-store" (NoStoreFilter), on both sites, so Back after a session
// ends can't bring it back from a cache. A full page was already, through its sign-out form's token; the fragments a
// page asks for as someone types (the patient search's results, the code search) were not. Static files stay
// cacheable, signed in or not.
public class NoStoreTests(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Signed_in_pages_are_never_stored_and_static_files_are()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Synthetic", "Nostore");
        await apps.AssignAsync(clinician, patient);
        var onSite = await SignInAsync(apps.OnPrem, clinician);
        var offSite = await SignInAsync(apps.Cloud, clinician);
        var admin = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Nostore Admin", StaffRoles.Admin));
        var (portal, _) = await PortalResultsTests.SignInPatientAsync(apps, apps.OnPrem, patient);

        foreach (var (client, path) in new[]
        {
            (onSite.Client, "/patients?handler=Results&name=Nos"),
            (onSite.Client, "/codes?kind=condition&q=dia"),
            (offSite.Client, "/patients?handler=Results&name=Nos"),
            (onSite.Client, "/patients"),
            (onSite.Client, "/quick-texts"),
            (onSite.Client, $"/patients/{patient}/vitals"),
            (onSite.Client, $"/patients/{patient}/messages/new"),
            (onSite.Client, $"/patients/{patient}/proxies"),
            (offSite.Client, $"/patients/{patient}/vitals"),
            (admin.Client, "/admin/audit"),
            (admin.Client, "/admin/staff"),
            (portal, "/portal"),
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (path.Contains('?', StringComparison.Ordinal))
            {
                // A fragment, asked for as a page's script asks.
                request.Headers.Add("HX-Request", "true");
            }

            using var response = await client.SendAsync(request, Cancel);
            Assert.Equal(new Uri(Base, path).AbsolutePath, response.RequestMessage!.RequestUri!.AbsolutePath);
            Assert.True(response.IsSuccessStatusCode, $"{path} answered {(int)response.StatusCode}.");
            Assert.True(response.Headers.CacheControl?.NoStore, $"{path} is not no-store.");
        }

        foreach (var client in new[] { onSite.Client, Client(apps.OnPrem, new System.Net.CookieContainer()) })
        {
            using var css = await client.GetAsync("/_content/Ehr.Design/css/ehr.css", Cancel);
            Assert.True(css.IsSuccessStatusCode);
            Assert.NotEqual(true, css.Headers.CacheControl?.NoStore);
        }
    }
}
