using System.Net;
using System.Text.RegularExpressions;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// UC5 "Register for a portal account", its expired-link course (Simar Rekhi): a confirmation link that no longer works
// offers a new one, the new link arrives by email and confirms the account, and the page answers the same for any
// address. Through the cloud app, as patients come. Supporting tests for UC5, not numbered test cases in the SE document.
public partial class PortalResendConfirmationTests(WebApps apps) : IClassFixture<WebApps>
{
    const string SentMessage = "If that address has a portal account waiting to be confirmed, we've sent it a new link.";

    [Fact]
    public async Task A_link_that_no_longer_works_offers_a_new_one()
    {
        var patient = await apps.InsertPatientAsync("Quinn", "Expired");
        var (email, _) = await apps.CreatePatientAccountAsync(patient, confirmed: false);
        var client = Client(apps.Cloud, new CookieContainer());

        // A token that doesn't verify, as an expired one doesn't.
        var path = $"/portal/confirm?user={Guid.CreateVersion7()}&token=expired";
        using var confirmed = await PostFormAsync(client, path, path);

        var html = await ReadAsync(confirmed);
        Assert.Contains("This link isn't valid or has expired.", html);
        Assert.Contains("href=\"/portal/resend\"", html);
        Assert.DoesNotContain(apps.Emails.Sent, m => m.To == email);
    }

    [Fact]
    public async Task A_new_link_goes_to_an_unconfirmed_account_and_confirms_it()
    {
        var patient = await apps.InsertPatientAsync("Rowan", "Unconfirmed");
        var (email, password) = await apps.CreatePatientAccountAsync(patient, confirmed: false);
        var client = Client(apps.Cloud, new CookieContainer());

        using var early = await PostFormAsync(client, "/portal/sign-in", "/portal/sign-in", ("Input.Email", email), ("Input.Password", password));
        Assert.Contains("If it expired, ask for a new one below.", await ReadAsync(early));

        using var asked = await PostFormAsync(client, "/portal/resend", "/portal/resend", ("Email", email));
        Assert.Contains(SentMessage, await ReadAsync(asked));
        var message = Assert.Single(apps.Emails.Sent, m => m.To == email);
        var confirmPath = new Uri(ConfirmLink().Match(message.Body).Value).PathAndQuery;

        using var confirmed = await PostFormAsync(client, confirmPath, confirmPath);
        Assert.Contains("Email address confirmed", await ReadAsync(confirmed));
        using var signedIn = await PostFormAsync(client, "/portal/sign-in", "/portal/sign-in", ("Input.Email", email), ("Input.Password", password));
        Assert.Equal("/portal", PathOf(signedIn));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.ConfirmationResent, patientId: patient));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.EmailConfirmed, patientId: patient));
    }

    [Fact]
    public async Task An_unknown_or_already_confirmed_address_gets_the_same_answer_and_no_email()
    {
        var unknown = WebApps.NewEmail();
        var (confirmed, _) = await apps.CreatePatientAccountAsync(await apps.InsertPatientAsync("Sage", "Confirmed"));
        var client = Client(apps.OnPrem, new CookieContainer());

        foreach (var email in new[] { unknown, confirmed })
        {
            using var asked = await PostFormAsync(client, "/portal/resend", "/portal/resend", ("Email", email));
            Assert.Contains(SentMessage, await ReadAsync(asked));
            Assert.DoesNotContain(apps.Emails.Sent, m => m.To == email);
        }
    }

    // An invalid entry is refused in place.
    [Fact]
    public async Task An_address_that_is_not_an_email_is_refused_in_place()
    {
        var client = Client(apps.OnPrem, new CookieContainer());

        using var asked = await PostFormAsync(client, "/portal/resend", "/portal/resend", ("Email", "jordan.lee@"));

        var html = await ReadAsync(asked);
        Assert.Contains("Enter a valid email address.", html);
        Assert.DoesNotContain(SentMessage, html);
    }

    [GeneratedRegex("https://[^\\s<]+/portal/confirm\\?user=[^\\s<]+")]
    private static partial Regex ConfirmLink();
}
