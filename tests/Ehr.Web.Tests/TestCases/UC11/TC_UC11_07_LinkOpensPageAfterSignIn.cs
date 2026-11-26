using System.Net;
using System.Text.RegularExpressions;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC11;

// Test Case ID: TC-UC11-07
// Purpose / scenario: A link through the staff sign-in opens its page alone, as the portal's does (the expansion plan's
//   step 13b): whatever query the link carries, signing in, with the code or with a first enrolment, or arriving already
//   signed in, goes on to the page it named without it, but for a patient search's criteria (name, date of birth,
//   MRN), which a search left when a session ended needs to come back. So a link can't show a clinician a patient's
//   sensitive items, recorded as their view, and a browser that also holds a portal session can't be sent through the
//   staff sign-in to the portal's file.
// Type: Functional (exceptional condition: a link chosen by someone else)
// Requirements: FR-27, proposed FR-39, proposed FR-56, NFR-06
// Use case: UC 11, Sign in with multi-factor authentication
// Preconditions: Dr. Staff Link has the patient on their panel; the patient's record holds a sensitive medication and
//   diagnosis, and the patient has a confirmed portal account. Dr. Staff Enrol, with the patient on their panel too, has
//   set a password and not yet enrolled an authenticator.
// Inputs and system conditions: on the on-site app, /sign-in with ReturnUrl=/portal/record?handler=File&sensitive=true
//   in a browser signed in to the portal; GET /patients/{id}?sensitive=true signed out, then the password and the code;
//   GET /sign-in?ReturnUrl=/patients/{id}?sensitive=true signed in; the same link signed out for the account that must
//   enrol, then the password, the enrolment code and "Continue"; signed in, /sign-in with ReturnUrl
//   /patients/{id}?sensitive=true&name=Fhirchart&handler=Assign&birthdate=1980-01-01.
// Test data: FhirRequests.ChartAsync's record.
// Steps:
//   1. In the browser signed in to the portal, sign in as Dr. Staff Link through the link to the portal's file.
//   2. Signed out, follow the link to the chart with sensitive items; sign in with the password and the code.
//   3. Signed in, open /sign-in with the same link as ReturnUrl; then with the link that also carries search criteria
//      and a handler.
//   4. Signed out, follow the same link as Dr. Staff Enrol; sign in, enrol, and select "Continue".
// Expected result: Step 1: the browser is on /portal/record, a page with its warning, not a file; no download is
//   recorded. Steps 2 to 4: each challenge carries the link as ReturnUrl; each ends on /patients/{id} with no query,
//   saying sensitive items are hidden and showing neither, but the link with criteria ends on
//   /patients/{id}?name=Fhirchart&birthdate=1980-01-01, without the rest; "Continue" goes to /patients/{id}. No view of
//   sensitive items is recorded for either clinician.
// Creator: Tahseen Arnan
public partial class TC_UC11_07_LinkOpensPageAfterSignIn(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_link_through_the_staff_sign_in_opens_its_page_alone()
    {
        var chart = await FhirRequests.ChartAsync(apps, "Dr. Staff Link");
        var clinician = chart.Clinician;
        var (email, password) = await apps.CreatePatientAccountAsync(chart.Patient);
        var account = await ProxyTests.AccountIdAsync(apps, email);
        var link = $"/patients/{chart.Patient}?sensitive=true";

        // 1. The reviewer's probe: a browser with a portal session, sent through the staff sign-in to the portal's file.
        var both = new CookieContainer();
        var browser = Client(apps.OnPrem, both);
        using (var portal = await PostFormAsync(browser, "/portal/sign-in", "/portal/sign-in", ("Input.Email", email), ("Input.Password", password)))
        {
            Assert.Equal("/portal", PathOf(portal));
        }

        const string file = "/portal/record?handler=File&sensitive=true";
        using (var landed = await SignInAsync(browser, clinician, "/sign-in?ReturnUrl=" + Uri.EscapeDataString(file)))
        {
            Assert.Equal("/portal/record", PathOf(landed));
            Assert.Equal("", landed.RequestMessage!.RequestUri!.Query);
            Assert.StartsWith("text/html", landed.Content.Headers.ContentType?.ToString(), StringComparison.Ordinal);
            Assert.Contains("Keep it private.", Words(await ReadAsync(landed)));
        }

        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.Download, account));
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.DownloadSensitive, account));

        // 2. A link to a chart with sensitive items, signed out: the password, then the code.
        var client = Client(apps.OnPrem, new CookieContainer());
        using (var landed = await SignInAsync(client, clinician, await ChallengeAsync(client, link)))
        {
            await OnTheChartAloneAsync(landed, chart.Patient);
        }

        // 3. The same link at the sign-in page, already signed in.
        using (var landed = await client.GetAsync("/sign-in?ReturnUrl=" + Uri.EscapeDataString(link), Cancel))
        {
            await OnTheChartAloneAsync(landed, chart.Patient);
        }

        // A link that carries a patient search's criteria keeps them, so a search left when a session ended comes back,
        // and nothing else: not sensitive items, not a handler.
        var criteria = $"/patients/{chart.Patient}?name=Fhirchart&birthdate=1980-01-01";
        using (var landed = await client.GetAsync("/sign-in?ReturnUrl=" + Uri.EscapeDataString($"/patients/{chart.Patient}?sensitive=true&name=Fhirchart&handler=Assign&birthdate=1980-01-01"), Cancel))
        {
            await OnTheChartAloneAsync(landed, chart.Patient, "?name=Fhirchart&birthdate=1980-01-01");
            Assert.Equal(criteria, landed.RequestMessage!.RequestUri!.PathAndQuery);
        }

        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.ReadSensitive, clinician.Id, chart.Patient));

        // 4. The same link for an account that must enrol its authenticator first.
        var newcomer = await apps.CreateStaffWithoutAuthenticatorAsync("Dr. Staff Enrol", StaffRoles.Clinician);
        await apps.AssignAsync(newcomer, chart.Patient);
        var fresh = Client(apps.OnPrem, new CookieContainer());
        var signIn = await ChallengeAsync(fresh, link);
        using var enrol = await PostFormAsync(fresh, signIn, signIn, ("Input.Email", newcomer.Email), ("Input.Password", newcomer.Password),
            ("ReturnUrl", link));
        Assert.Equal("/sign-in/enroll", PathOf(enrol));
        var key = AuthenticatorSecret().Match(await enrol.Content.ReadAsStringAsync(Cancel)).Groups[1].Value;
        using var codes = await PostFormAsync(fresh, enrol.RequestMessage!.RequestUri!.PathAndQuery, enrol, ("Code", Totp.Code(key)), ("ReturnUrl", link));
        var codesPage = await ReadAsync(codes);
        Assert.Contains("Your recovery codes", codesPage);
        var next = WebUtility.HtmlDecode(ContinueLink().Match(codesPage).Groups[1].Value);
        Assert.Equal($"/patients/{chart.Patient}", next);
        using (var landed = await fresh.GetAsync(next, Cancel))
        {
            await OnTheChartAloneAsync(landed, chart.Patient);
        }

        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.ReadSensitive, newcomer.Id, chart.Patient));
    }

    // Follows a link signed out: the staff sign-in page, with the link as its ReturnUrl.
    static async Task<string> ChallengeAsync(HttpClient client, string link)
    {
        using var challenged = await client.GetAsync(link, Cancel);
        Assert.Equal("/sign-in", PathOf(challenged));
        Assert.Equal(link, QueryHelpers.ParseQuery(challenged.RequestMessage!.RequestUri!.Query)["ReturnUrl"].ToString());
        return challenged.RequestMessage.RequestUri.PathAndQuery;
    }

    // The password at the sign-in page given, then the code, as a browser posts them; the page the sign-in ends on.
    static async Task<HttpResponseMessage> SignInAsync(HttpClient client, StaffAccount clinician, string signIn)
    {
        var returnUrl = QueryHelpers.ParseQuery(new Uri(Base, signIn).Query)["ReturnUrl"].ToString();
        using var codePage = await PostFormAsync(client, signIn, signIn, ("Input.Email", clinician.Email), ("Input.Password", clinician.Password),
            ("ReturnUrl", returnUrl));
        Assert.Equal("/sign-in/code", PathOf(codePage));
        return await PostFormAsync(client, codePage.RequestMessage!.RequestUri!.PathAndQuery, codePage, ("Code", Totp.Code(clinician.AuthenticatorKey)),
            ("ReturnUrl", returnUrl));
    }

    // The chart, with no query but the search criteria given: sensitive items hidden, and neither of them on the page.
    static async Task OnTheChartAloneAsync(HttpResponseMessage landed, Guid patient, string query = "")
    {
        Assert.Equal(HttpStatusCode.OK, landed.StatusCode);
        Assert.Equal($"/patients/{patient}", PathOf(landed));
        Assert.Equal(query, landed.RequestMessage!.RequestUri!.Query);
        var words = Words(await ReadAsync(landed));
        Assert.Contains("Sensitive items hidden:", words);
        Assert.DoesNotContain("sertraline", words);
        Assert.DoesNotContain("Generalized anxiety disorder", words);
    }

    [GeneratedRegex("secret=([A-Z2-7]+)")]
    private static partial Regex AuthenticatorSecret();

    [GeneratedRegex("<a href=\"([^\"]*)\" class=\"sc-btn sc-btn--primary\">Continue</a>")]
    private static partial Regex ContinueLink();
}
