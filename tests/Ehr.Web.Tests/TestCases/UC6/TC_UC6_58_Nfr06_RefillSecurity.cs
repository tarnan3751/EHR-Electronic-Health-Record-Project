using System.Net;
using System.Text.RegularExpressions;
using Ehr.Data.Audit;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RefillRequestTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-58
// Purpose / scenario: The refill page shows no identifier of the clinic's: no ID of the patient, a medication, a request,
//   an order or a clinician, in any form, and no record number or drug code, data attributes included; a medication is
//   named by a reference the app protects. It is never cached; HEAD, which would read and record a view for nothing
//   shown, is refused; a post without the page's form token is refused; no answer has a CORS header; and a link that
//   asks for sensitive items opens the page without them after sign-in.
// Type: Non-functional (security)
// Requirements: NFR-06, proposed FR-58
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a portal account, signed in on the on-site app; Dr. Test has the patient on their panel.
//   The patient has asked for lisinopril, which Dr. Test declined; amlodipine is offered.
// Inputs and system conditions: GET, HEAD and POST /portal/refills, and GET /portal/refills?sensitive=true signed out,
//   on the on-site app.
// Test data: lisinopril 10 MG Oral Tablet (RxNorm 314076), amlodipine 5 MG Oral Tablet (197361), sertraline 50 MG Oral
//   Tablet marked sensitive.
// Steps:
//   1. Open the page, with an Origin header from another site, and read its markup and headers.
//   2. Send HEAD for the page.
//   3. Post a request without the form token.
//   4. Signed out, follow /portal/refills?sensitive=true and sign in.
// Expected result: Step 1: Cache-Control: no-store, no Access-Control-Allow-Origin; no UUID, record number or drug code
//   in the markup (the form token and the account's own address aside); data attributes hold only the page's own words.
//   Step 2: 405, and no read recorded. Step 3: 400, nothing saved. Step 4: the page without its query, sensitive items
//   hidden, and no view of them recorded.
// Creator: Tahseen Arnan
public class TC_UC6_58_Nfr06_RefillSecurity(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_refill_page_shows_no_identifier_and_takes_nothing_it_should_not()
    {
        var (clinician, session, patient) = await PortalPatientAsync(apps, "Guardedrefill");
        var lisinopril = await MedicationAsync(apps, patient.Patient);
        var amlodipine = await MedicationAsync(apps, patient.Patient, "amlodipine 5 MG Oral Tablet", "197361");
        await MedicationAsync(apps, patient.Patient, "sertraline 50 MG Oral Tablet", "312941", sensitive: true);
        using (var sent = await AskAsync(patient.Client, Lisinopril, "Synthetic note."))
        {
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        }

        var request = await RequestIdAsync(apps, patient.Patient);
        using (var declined = await DeclineAsync(session.Client, patient.Patient, request, "Synthetic: no."))
        {
            Assert.Equal(HttpStatusCode.OK, declined.StatusCode);
        }

        using var get = new HttpRequestMessage(HttpMethod.Get, PortalPath);
        get.Headers.Add("Origin", "https://elsewhere.example");
        using var response = await patient.Client.SendAsync(get, Cancel);
        var html = await ReadAsync(response);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Contains("Synthetic: no.", Words(html));
        var markup = WithoutTokens(html).Replace(patient.Email, "", StringComparison.OrdinalIgnoreCase);
        foreach (var identifier in new[] { patient.Patient, lisinopril, amlodipine, request, clinician.Id, patient.Account })
        {
            Assert.DoesNotContain(identifier.ToString(), markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(identifier.ToString("N"), markup, StringComparison.OrdinalIgnoreCase);
        }

        Assert.False(HasAnId(markup));
        Assert.DoesNotContain(WebApps.MrnOf(patient.Patient), markup);
        foreach (var code in new[] { "314076", "197361", "312941", "SYN-MED" })
        {
            Assert.DoesNotContain(code, Words(markup));
        }

        Assert.All(Regex.Matches(markup, "data-[a-z-]+=\"([^\"]*)\"").Select(m => m.Groups[1].Value),
            value => Assert.Contains(value, new[] { "ask", "rejected", "requested", "completed", "hidden", "shown", "" }.Concat(SessionBrowser.PortalSettings)));

        var reads = await apps.AuditEventsAsync(AuditActions.Read, patient.Account, patient.Patient);
        using (var head = await patient.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, PortalPath), Cancel))
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, head.StatusCode);
        }

        Assert.Equal(reads, await apps.AuditEventsAsync(AuditActions.Read, patient.Account, patient.Patient));

        var page = await patient.Client.GetStringAsync(PortalPath, Cancel);
        using (var forged = await patient.Client.PostAsync(PortalPath, Form(("Medication", ReferenceOf(page, "amlodipine 5 MG Oral Tablet")), ("Note", "")), Cancel))
        {
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        }

        Assert.Single(await RequestsAsync(apps, patient.Patient));

        var sensitiveViews = await apps.AuditEventsAsync(AuditActions.ReadSensitive, patient.Account);
        var client = Client(apps.OnPrem, new CookieContainer());
        using var challenged = await client.GetAsync(PortalPath + "?sensitive=true", Cancel);
        Assert.Equal("/portal/sign-in", PathOf(challenged));
        var returnUrl = QueryHelpers.ParseQuery(challenged.RequestMessage!.RequestUri!.Query)["ReturnUrl"].ToString();
        using var landed = await PostFormAsync(client, "/portal/sign-in", challenged,
            ("Input.Email", patient.Email), ("Input.Password", ProxyTests.Password), ("ReturnUrl", returnUrl));
        var words = Words(await ReadAsync(landed));
        Assert.Equal(PortalPath, PathOf(landed));
        Assert.Equal("", landed.RequestMessage!.RequestUri!.Query);
        Assert.Contains("Sensitive items hidden.", words);
        Assert.DoesNotContain("sertraline", words);
        Assert.Equal(sensitiveViews, await apps.AuditEventsAsync(AuditActions.ReadSensitive, patient.Account));
    }
}
