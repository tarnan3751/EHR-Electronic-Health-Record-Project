using System.Net;
using System.Text.RegularExpressions;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// A patient's forgotten password (Areas/Portal/Pages/Account/Forgot and Reset, PatientAccounts), through the cloud app
// as patients come: the link arrives by email, works once, and the old password is over.
public partial class PortalPasswordResetTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_patient_resets_a_forgotten_password_through_the_emailed_link()
    {
        var patient = await apps.InsertPatientAsync("Lee", "Forgetful");
        var (email, password) = await apps.CreatePatientAccountAsync(patient);
        var client = Client(apps.Cloud, new CookieContainer());

        using var asked = await PostFormAsync(client, "/portal/forgot", "/portal/forgot", ("Email", email));
        Assert.Contains("we have sent it a link", await ReadAsync(asked));
        var message = Assert.Single(apps.Emails.Sent, m => m.To == email && m.Subject.Contains("Reset"));
        var resetPath = new Uri(ResetLink().Match(message.Body).Value).PathAndQuery;

        const string newPassword = "a-new-portal-password";
        using var changed = await PostFormAsync(client, resetPath, resetPath, ("Input.Password", newPassword), ("Input.Confirm", newPassword));
        Assert.Contains("Your password is changed.", await ReadAsync(changed));

        using var again = await PostFormAsync(client, resetPath, resetPath, ("Input.Password", newPassword), ("Input.Confirm", newPassword));
        Assert.Contains("is not valid or has already been used", await ReadAsync(again));

        using var oldOne = await PostFormAsync(client, "/portal/sign-in", "/portal/sign-in", ("Input.Email", email), ("Input.Password", password));
        Assert.Contains("Wrong email or password.", await ReadAsync(oldOne));
        using var newOne = await PostFormAsync(client, "/portal/sign-in", "/portal/sign-in", ("Input.Email", email), ("Input.Password", newPassword));
        Assert.Equal("/portal", PathOf(newOne));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.PasswordResetRequested, patientId: patient));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.PasswordChanged, patientId: patient));
    }

    [Fact]
    public async Task An_unknown_address_gets_the_same_answer_and_no_email()
    {
        var email = WebApps.NewEmail();
        var client = Client(apps.OnPrem, new CookieContainer());

        using var asked = await PostFormAsync(client, "/portal/forgot", "/portal/forgot", ("Email", email));

        Assert.Contains("we have sent it a link", await ReadAsync(asked));
        Assert.DoesNotContain(apps.Emails.Sent, m => m.To == email);
    }

    [GeneratedRegex("https://[^\\s<]+/portal/reset\\?user=[^\\s<]+")]
    private static partial Regex ResetLink();
}
