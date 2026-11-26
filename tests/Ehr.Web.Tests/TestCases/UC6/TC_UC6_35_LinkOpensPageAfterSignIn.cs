using System.Net;
using Ehr.Data.Audit;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.RecordDownload;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-35
// Purpose / scenario: A link from anywhere that asks the portal for the record's file, or for sensitive items, can't get
//   them through sign-in. Followed while signed out (as from another site, which the sign-in cookie never reaches), it
//   is sent to sign in; signing in goes on to the page the link named, without its query: Download my record with its
//   warning and the box unticked, the printable page or Visit summaries without sensitive items. Nothing is downloaded
//   and nothing sensitive is shown or recorded; whatever the person wants, they ask for on the page.
// Type: Functional (exceptional condition: a link chosen by someone else)
// Requirements: proposed FR-56, proposed FR-55, NFR-06
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: As TC-UC6-24: the patient's record holds a sensitive medication and diagnosis, and Dr. Linked has
//   released a summary ticked "Sensitive". The patient has given access, sensitive items included, to someone who is a
//   patient too. Neither is signed in on the browser that follows the links.
// Inputs and system conditions: GET /portal/record?handler=File&sensitive=true, /portal/record/print?sensitive=true and
//   /portal/summaries?sensitive=true, signed out, then POST /portal/sign-in with the ReturnUrl the challenge gave; then,
//   signed in, GET /portal/sign-in with each link as ReturnUrl; on the on-site app; as the patient, then as the proxy.
// Test data: FhirRequests.ChartAsync's record; a summary "We talked about something private.", ticked "Sensitive".
// Steps:
//   1. Signed out, follow each link; sign in on the page it leads to.
//   2. Signed in, open the sign-in page with each link as ReturnUrl.
// Expected result: Each link leads to /portal/sign-in with the link as its ReturnUrl. After sign-in, the browser is on
//   the link's page with no query: /portal/record, a page (not a file) with "Keep it private." and the box unticked;
//   /portal/record/print and /portal/summaries, saying sensitive items are hidden, without them. Step 2 ends on the
//   same pages, with no query. No download of any
//   kind and no view of sensitive items is recorded under either account; the proxy, whose sign-in starts on their own
//   record, gets nothing of the patient's.
// Creator: Tahseen Arnan
public class TC_UC6_35_LinkOpensPageAfterSignIn(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_link_asking_for_the_file_or_sensitive_items_lands_on_its_page_alone_after_sign_in()
    {
        var (chart, patient) = await PortalChartAsync(apps, "Dr. Linked");
        var note = await SignedNoteAsync(chart.Session.Client, chart.Patient);
        using (var released = await ReleaseAsync(chart.Session.Client, chart.Patient, note, "We talked about something private.", sensitive: true))
        {
            Assert.Contains("Released.", Words(await ReadAsync(released)));
        }

        var (proxy, _) = await ProxyForAsync(apps, patient, "Linkedproxy", sensitive: true);
        var links = new[] { FilePath + "&sensitive=true", PrintPath + "?sensitive=true", "/portal/summaries?sensitive=true" };
        var proxysViews = await apps.AuditEventsAsync(AuditActions.Read, proxy.Account, patient.Patient);
        foreach (var person in new[] { patient, proxy })
        {
            // Every kind of download, and views of sensitive items, under this account so far.
            var asked = new Dictionary<string, long>();
            foreach (var action in new[] { AuditActions.Download, AuditActions.DownloadSensitive, AuditActions.ReadSensitive })
            {
                asked[action] = await apps.AuditEventsAsync(action, person.Account);
            }

            foreach (var link in links)
            {
                var client = Client(apps.OnPrem, new CookieContainer());
                using var challenged = await client.GetAsync(link, Cancel);
                Assert.Equal("/portal/sign-in", PathOf(challenged));
                var returnUrl = QueryHelpers.ParseQuery(challenged.RequestMessage!.RequestUri!.Query)["ReturnUrl"].ToString();
                Assert.Equal(link, returnUrl);

                using var landed = await PostFormAsync(client, "/portal/sign-in", challenged,
                    ("Input.Email", person.Email), ("Input.Password", ProxyTests.Password), ("ReturnUrl", returnUrl));
                var page = await ReadAsync(landed);
                var words = Words(page);
                Assert.Equal(HttpStatusCode.OK, landed.StatusCode);
                Assert.Equal(link[..link.IndexOf('?', StringComparison.Ordinal)], PathOf(landed));
                Assert.Equal("", landed.RequestMessage!.RequestUri!.Query);
                Assert.StartsWith("text/html", landed.Content.Headers.ContentType?.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain("Sensitive items shown", words);
                Assert.DoesNotContain("something private", words);
                Assert.DoesNotContain("sertraline", words);
                if (link.StartsWith(FilePath, StringComparison.Ordinal))
                {
                    Assert.Contains("Keep it private.", words);
                    Assert.Contains("name=\"sensitive\" value=\"true\" aria-describedby", page);
                }

                if (person == proxy)
                {
                    Assert.DoesNotContain("Fhirchart", page);
                }
            }

            // Signing in started from the proxy's own record: nothing of the patient's was read.
            Assert.Equal(proxysViews, await apps.AuditEventsAsync(AuditActions.Read, proxy.Account, patient.Patient));

            // Already signed in: the same links at the sign-in page go on to their pages alone too.
            foreach (var link in links)
            {
                using var landed = await person.Client.GetAsync("/portal/sign-in?ReturnUrl=" + Uri.EscapeDataString(link), Cancel);
                Assert.Equal(HttpStatusCode.OK, landed.StatusCode);
                Assert.Equal(link[..link.IndexOf('?', StringComparison.Ordinal)], PathOf(landed));
                Assert.Equal("", landed.RequestMessage!.RequestUri!.Query);
                Assert.StartsWith("text/html", landed.Content.Headers.ContentType?.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain("Sensitive items shown", Words(await ReadAsync(landed)));
            }

            foreach (var (action, before) in asked)
            {
                Assert.Equal(before, await apps.AuditEventsAsync(action, person.Account));
            }
        }
    }
}
