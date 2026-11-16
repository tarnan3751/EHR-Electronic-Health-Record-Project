using System.Net;
using System.Text.RegularExpressions;
using Ehr.Data.Audit;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
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

        // Four reads of this patient: the clinician's panel when they signed in, their chart view, the chart again when
        // the code was issued, and the portal view by the patient's account, which the cloud app served from its
        // replica, queued, and shipped to the on-prem app.
        await apps.ShipCloudAuditAsync();
        Assert.Equal(3, await apps.AuditEventsAsync(AuditActions.Read, actorId: clinician.Id, patientId: own));
        Assert.Equal(4, await apps.AuditEventsAsync(AuditActions.Read, patientId: own));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Read, patientId: own, source: AuditSources.Cloud));
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
        // Signed in first: the home lists the panel, and that would be a read too.
        var session = await SignInAsync(apps.OnPrem, clinician);
        await apps.AssignAsync(clinician, patient);

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

        var byName = await session.Client.GetStringAsync("/patients?name=Searchable", Cancel);
        var byMrn = await session.Client.GetStringAsync($"/patients?mrn={patient.ToString("N")[^12..]}", Cancel);

        Assert.Contains($"/patients/{patient}", byName);
        Assert.Contains($"/patients/{patient}", byMrn);
        Assert.Equal(2, await apps.AuditEventsAsync(AuditActions.Read, actorId: clinician.Id, patientId: patient));
    }

    [Fact]
    public async Task With_the_on_prem_app_unreachable_phi_pages_are_read_only_off_site()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Finley", "Remote");
        // Signed in first: the home lists the panel, and that would be a read too.
        var signedIn = await CookieHeaderAsync(apps.Cloud, clinician);
        await apps.AssignAsync(clinician, patient);
        // The cloud app finds out within a couple of seconds; this is that check, now.
        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));

        using var client = CreateClient(apps.CloudCutOff, handleCookies: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/patients/{patient}");
        request.Headers.Add("Cookie", signedIn);
        using var response = await client.SendAsync(request, Cancel);

        var html = await ReadAsync(response);
        Assert.Contains("Remote, Finley", html);
        Assert.Contains("Read-only: the clinic's server can't be reached", html);
        // Recorded in the cloud app's queue, waiting for the on-prem app.
        var (queued, _) = apps.CloudCutOff.Services.GetRequiredService<CloudAuditQueue>().ReadPending(int.MaxValue);
        Assert.Contains(queued, e => e.Action == AuditActions.Read && e.PatientId == patient && e.ActorId == clinician.Id);
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.Read, actorId: clinician.Id, patientId: patient));
    }

    [Fact]
    public async Task Off_site_a_patient_signs_out_while_the_on_prem_app_is_unreachable()
    {
        var patient = await apps.InsertPatientAsync("Quinn", "Leaving");
        var (email, password) = await apps.CreatePatientAccountAsync(patient);
        var jar = new CookieContainer();
        using (var signedIn = await PostFormAsync(Client(apps.Cloud, jar), "/portal/sign-in", "/portal/sign-in", ("Input.Email", email), ("Input.Password", password)))
        {
            Assert.Equal("/portal", PathOf(signedIn));
        }

        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));

        using var signedOut = await PostFormAsync(Client(apps.CloudCutOff, jar), "/portal/sign-out", "/portal");

        Assert.Contains("Signed out", await ReadAsync(signedOut));
        using var record = await Client(apps.CloudCutOff, jar, followRedirects: false).GetAsync("/portal", Cancel);
        Assert.Equal("/portal/sign-in", new Uri(Base, record.Headers.Location!).AbsolutePath);
        var (queued, _) = apps.CloudCutOff.Services.GetRequiredService<CloudAuditQueue>().ReadPending(int.MaxValue);
        Assert.Contains(queued, e => e.Action == AuditActions.SignOut && e.PatientId == patient && e.ActorId is not null && e.TableName is null);
    }

    // TC-UC5-09 (Simar Rekhi): UC5's "email already registered" course. The page answers as it does for a registration
    // that worked, the address's owner is told by email and offered sign-in, and the code stays unused, so the patient
    // can register with another address.
    [Fact]
    public async Task An_address_that_already_has_an_account_is_offered_sign_in_and_the_code_stays_usable()
    {
        var clinician = await apps.ClinicianAsync();
        var (taken, _) = await apps.CreatePatientAccountAsync(await apps.InsertPatientAsync("Drew", "Registered"));
        var patient = await apps.InsertPatientAsync("Ellis", "Newcomer");
        var code = await IssueCodeAsync(clinician, patient);
        var client = Client(apps.Cloud, new CookieContainer());
        const string password = "a-patient-portal-password";

        using var answered = await PostFormAsync(client, "/portal/register", "/portal/register",
            ("Input.Code", code), ("Input.Email", taken), ("Input.Password", password), ("Input.Confirm", password));
        var html = await ReadAsync(answered);
        Assert.Contains("Check your email", html);
        Assert.DoesNotContain("already", html);
        var message = Assert.Single(apps.Emails.Sent, m => m.To == taken);
        Assert.Contains("Someone tried to register a patient portal account with this address.", message.Body);
        Assert.Contains("/portal/sign-in", message.Body);
        Assert.Contains("/portal/forgot", message.Body);
        Assert.DoesNotContain("/portal/confirm", message.Body);

        using var registered = await PostFormAsync(client, "/portal/register", "/portal/register",
            ("Input.Code", code), ("Input.Email", WebApps.NewEmail()), ("Input.Password", password), ("Input.Confirm", password));
        Assert.Contains("Check your email", await ReadAsync(registered));
    }

    // One valid code, tried against other patients' addresses, learns nothing: each answer is the page a registration
    // that worked gets, status and HTML, and the code still works afterwards (issue #34).
    [Fact]
    public async Task One_valid_code_cant_tell_which_addresses_belong_to_other_patients()
    {
        var clinician = await apps.ClinicianAsync();
        var (confirmed, _) = await apps.CreatePatientAccountAsync(await apps.InsertPatientAsync("Avery", "Confirmed"));
        var (unconfirmed, _) = await apps.CreatePatientAccountAsync(await apps.InsertPatientAsync("Blake", "Unconfirmed"), confirmed: false);
        var nobody = WebApps.NewEmail();
        var code = await IssueCodeAsync(clinician, await apps.InsertPatientAsync("Casey", "Prober"));
        var client = Client(apps.Cloud, new CookieContainer());
        const string password = "a-patient-portal-password";

        async Task<(HttpStatusCode Status, string Html)> RegisterAsync(string address, string withPassword)
        {
            using var response = await PostFormAsync(client, "/portal/register", "/portal/register",
                ("Input.Code", code), ("Input.Email", address), ("Input.Password", withPassword), ("Input.Confirm", withPassword));
            var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            return (response.StatusCode, FormToken().Replace(html, "").Replace(address, "ADDRESS"));
        }

        var first = await RegisterAsync(confirmed, password);
        var second = await RegisterAsync(unconfirmed, password);
        // The unconfirmed account gets a fresh confirmation link, as the resend page sends.
        Assert.Contains("/portal/confirm", Assert.Single(apps.Emails.Sent, m => m.To == unconfirmed).Body);

        // The code was never spent: it registers an unknown address, and that answer is the one the probes got.
        var worked = await RegisterAsync(nobody, password);
        Assert.Contains("Check your email", worked.Html);
        Assert.Equal(worked, first);
        Assert.Equal(worked, second);
    }

    // Issue #33: a registration with a mistyped address is never confirmed. A new code from the clinic takes the
    // account over: new address, new password, and the old address's link stops working.
    [Fact]
    public async Task A_mistyped_registration_is_recovered_with_a_new_code()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Finley", "Mistyped");
        var client = Client(apps.Cloud, new CookieContainer());
        var (mistyped, right) = (WebApps.NewEmail(), WebApps.NewEmail());
        const string password = "a-patient-portal-password";
        const string newPassword = "another-portal-password";

        using var first = await PostFormAsync(client, "/portal/register", "/portal/register",
            ("Input.Code", await IssueCodeAsync(clinician, patient)), ("Input.Email", mistyped), ("Input.Password", password), ("Input.Confirm", password));
        Assert.Contains("Check your email", await ReadAsync(first));
        var oldPath = new Uri(ConfirmLink().Match(Assert.Single(apps.Emails.Sent, m => m.To == mistyped).Body).Value).PathAndQuery;

        using var second = await PostFormAsync(client, "/portal/register", "/portal/register",
            ("Input.Code", await IssueCodeAsync(clinician, patient, assign: false)), ("Input.Email", right), ("Input.Password", newPassword), ("Input.Confirm", newPassword));
        Assert.Contains("Check your email", await ReadAsync(second));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.RegistrationReplaced, patientId: patient));

        using var old = await PostFormAsync(client, oldPath, oldPath);
        Assert.Contains("This link is not valid or has expired.", await ReadAsync(old));

        var newPath = new Uri(ConfirmLink().Match(Assert.Single(apps.Emails.Sent, m => m.To == right).Body).Value).PathAndQuery;
        using var confirmed = await PostFormAsync(client, newPath, newPath);
        Assert.Contains("Your address is confirmed", await ReadAsync(confirmed));
        using var signedIn = await PostFormAsync(client, "/portal/sign-in", "/portal/sign-in", ("Input.Email", right), ("Input.Password", newPassword));
        Assert.Equal("/portal", PathOf(signedIn));
        using var oldAddress = await PostFormAsync(Client(apps.Cloud, new CookieContainer()), "/portal/sign-in", "/portal/sign-in", ("Input.Email", mistyped), ("Input.Password", password));
        Assert.NotEqual("/portal", PathOf(oldAddress));

        // Nobody was signed in for any of it, so the events name the patient and no actor.
        foreach (var action in new[] { AuditActions.PatientRegistered, AuditActions.RegistrationReplaced, AuditActions.EmailConfirmed })
        {
            Assert.Equal(1, await apps.AuditEventsWithoutActorAsync(action, patient));
        }
    }

    [Fact]
    public async Task A_confirmed_account_cant_be_taken_over_with_a_new_code()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Gray", "Settled");
        var (email, password) = await apps.CreatePatientAccountAsync(patient);
        var code = await IssueCodeAsync(clinician, patient);
        var client = Client(apps.Cloud, new CookieContainer());
        var other = WebApps.NewEmail();

        using var refused = await PostFormAsync(client, "/portal/register", "/portal/register",
            ("Input.Code", code), ("Input.Email", other), ("Input.Password", "another-portal-password"), ("Input.Confirm", "another-portal-password"));

        Assert.Contains("That enrollment code is not valid or has expired.", await ReadAsync(refused));
        Assert.DoesNotContain(apps.Emails.Sent, m => m.To == other);
        using var signedIn = await PostFormAsync(client, "/portal/sign-in", "/portal/sign-in", ("Input.Email", email), ("Input.Password", password));
        Assert.Equal("/portal", PathOf(signedIn));
    }

    // TC-UC5-10 (Simar Rekhi): invalid entries are named in place, and nothing is created.
    [Fact]
    public async Task A_short_password_or_a_mismatch_is_named_in_place()
    {
        var client = Client(apps.OnPrem, new CookieContainer());
        var email = WebApps.NewEmail();

        using var refused = await PostFormAsync(client, "/portal/register", "/portal/register",
            ("Input.Code", "AAAA-BBBB-CCCC"), ("Input.Email", email), ("Input.Password", "short"), ("Input.Confirm", "different"));

        var html = await ReadAsync(refused);
        Assert.Contains("Use at least 12 characters.", html);
        // The page's HTML encodes the apostrophe.
        Assert.Contains("The passwords don&#x27;t match.", html);
        Assert.DoesNotContain(apps.Emails.Sent, m => m.To == email);
    }

    // A clinician issues one of their patients an enrollment code from the chart, and reads it off the page.
    async Task<string> IssueCodeAsync(StaffAccount clinician, Guid patient, bool assign = true)
    {
        if (assign)
        {
            await apps.AssignAsync(clinician, patient);
        }

        var session = await SignInAsync(apps.OnPrem, clinician);
        using var response = await PostFormAsync(session.Client, $"/patients/{patient}?handler=Enroll", $"/patients/{patient}");
        var match = EnrollmentCode().Match(await ReadAsync(response));
        Assert.True(match.Success, "No enrollment code on the page.");
        return match.Groups[1].Value;
    }

    [GeneratedRegex("<p class=\"code\">([A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4})</p>")]
    private static partial Regex EnrollmentCode();

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*>")]
    private static partial Regex FormToken();

    [GeneratedRegex("https://[^\\s<]+/portal/confirm\\?user=[^\\s<]+")]
    private static partial Regex ConfirmLink();
}
