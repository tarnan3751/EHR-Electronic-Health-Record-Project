using System.Net;
using System.Text.RegularExpressions;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The patient portal end to end, across both sites: a clinician issues an enrollment code from the chart on-prem, the
// patient registers through the cloud app, confirms their address, signs in, and sees their own record and nobody
// else's. And the chart view and the portal view are both in the audit log.
public partial class PortalTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_patient_registers_with_a_code_confirms_signs_in_and_sees_only_their_record()
    {
        var clinician = await apps.ClinicianAsync();
        var own = await apps.InsertPatientAsync("Alex", "Example");
        await apps.InsertPatientAsync("Blair", "Other");
        var code = await IssueCodeAsync(clinician, own);

        // Registration, through the cloud app: the form comes from the replica, the post goes to on-prem.
        var jar = new CookieContainer();
        var client = Client(apps.Cloud, jar);
        var email = WebApps.NewEmail();
        const string password = "a-patient-portal-password";
        using var registered = await PostFormAsync(client, "/portal/register", "/portal/register",
            ("Input.Code", code), ("Input.Email", email), ("Input.Password", password), ("Input.Confirm", password));
        Assert.Contains("Check your email", await ReadAsync(registered));

        // Not before confirming.
        using var early = await PostFormAsync(client, "/portal/sign-in", "/portal/sign-in", ("Input.Email", email), ("Input.Password", password));
        Assert.Contains("Confirm your email address first", await ReadAsync(early));

        var message = Assert.Single(apps.Emails.Sent, m => m.To == email);
        var confirmPath = new Uri(ConfirmLink().Match(message.Body).Value).PathAndQuery;
        using var confirmed = await PostFormAsync(client, confirmPath, confirmPath);
        Assert.Contains("Your address is confirmed", await ReadAsync(confirmed));

        using var signedIn = await PostFormAsync(client, "/portal/sign-in", "/portal/sign-in", ("Input.Email", email), ("Input.Password", password));
        Assert.Equal("/portal", PathOf(signedIn));
        var record = await ReadAsync(signedIn);
        Assert.Contains("Alex Example", record);
        Assert.DoesNotContain("Blair", record);

        // Three reads of this patient: the clinician's chart view, the chart again when the code was issued, and the
        // portal view by the patient's account, recorded on the on-prem app it was forwarded to.
        Assert.Equal(2, await apps.AuditEventsAsync(AuditActions.Read, actorId: clinician.Id, patientId: own));
        Assert.Equal(3, await apps.AuditEventsAsync(AuditActions.Read, patientId: own));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.PatientRegistered, patientId: own));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.EnrollmentIssued, actorId: clinician.Id, patientId: own));
    }

    [Fact]
    public async Task A_code_that_was_never_issued_or_was_used_is_refused()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Casey", "Reused");
        var code = await IssueCodeAsync(clinician, patient);
        var client = Client(apps.OnPrem, new CookieContainer());
        const string password = "a-patient-portal-password";

        using var invented = await PostFormAsync(client, "/portal/register", "/portal/register",
            ("Input.Code", "AAAA-BBBB-CCCC"), ("Input.Email", WebApps.NewEmail()), ("Input.Password", password), ("Input.Confirm", password));
        Assert.Contains("is not valid or has expired", await ReadAsync(invented));

        using var first = await PostFormAsync(client, "/portal/register", "/portal/register",
            ("Input.Code", code.ToLowerInvariant()), ("Input.Email", WebApps.NewEmail()), ("Input.Password", password), ("Input.Confirm", password));
        Assert.Contains("Check your email", await ReadAsync(first));

        using var second = await PostFormAsync(client, "/portal/register", "/portal/register",
            ("Input.Code", code), ("Input.Email", WebApps.NewEmail()), ("Input.Password", password), ("Input.Confirm", password));
        Assert.Contains("is not valid or has expired", await ReadAsync(second));
    }

    [Fact]
    public async Task A_chart_view_is_a_recorded_read_by_the_clinician()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Dana", "Viewed");
        var session = await SignInAsync(apps.OnPrem, clinician);

        var html = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);

        Assert.Contains("Viewed, Dana", html);
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Read, actorId: clinician.Id, patientId: patient));
    }

    [Fact]
    public async Task Search_finds_by_name_or_mrn_and_records_each_result()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Evan", "Searchable" + Guid.NewGuid().ToString("N")[..6]);
        var session = await SignInAsync(apps.OnPrem, clinician);

        var byName = await session.Client.GetStringAsync("/patients?q=Searchable", Cancel);
        var byMrn = await session.Client.GetStringAsync($"/patients?q={patient.ToString("N")[..12]}", Cancel);

        Assert.Contains($"/patients/{patient}", byName);
        Assert.Contains($"/patients/{patient}", byMrn);
        Assert.Equal(2, await apps.AuditEventsAsync(AuditActions.Read, actorId: clinician.Id, patientId: patient));
    }

    [Fact]
    public async Task With_the_on_prem_app_unreachable_phi_pages_are_unavailable_off_site()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Finley", "Remote");
        var signedIn = await CookieHeaderAsync(apps.Cloud, clinician);

        using var client = CreateClient(apps.CloudCutOff, handleCookies: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/patients/{patient}");
        request.Headers.Add("Cookie", signedIn);
        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("can't be reached", await response.Content.ReadAsStringAsync(Cancel));
    }

    // A clinician issues the patient an enrollment code from the chart, and reads it off the page.
    async Task<string> IssueCodeAsync(StaffAccount clinician, Guid patient)
    {
        var session = await SignInAsync(apps.OnPrem, clinician);
        using var response = await PostFormAsync(session.Client, $"/patients/{patient}?handler=Enroll", $"/patients/{patient}");
        var match = EnrollmentCode().Match(await ReadAsync(response));
        Assert.True(match.Success, "No enrollment code on the page.");
        return match.Groups[1].Value;
    }

    [GeneratedRegex("<p class=\"code\">([A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4})</p>")]
    private static partial Regex EnrollmentCode();

    [GeneratedRegex("https://[^\\s<]+/portal/confirm\\?user=[^\\s<]+")]
    private static partial Regex ConfirmLink();
}
