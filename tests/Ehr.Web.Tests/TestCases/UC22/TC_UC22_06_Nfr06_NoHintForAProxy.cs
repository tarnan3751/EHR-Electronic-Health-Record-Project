using System.Net;
using System.Text.RegularExpressions;
using Ehr.Data;
using Ehr.Data.Audit;
using Ehr.Domain.Audit;
using Ehr.Domain.Phi;
using Ehr.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.PreVisitFormTests;

namespace Ehr.Web.Tests.TestCases.UC22;

// Test Case ID: TC-UC22-06
// Purpose / scenario: Someone a patient shares their record with, without sensitive items, can't learn that the record
//   has pre-visit forms: no link, no count, no form, and a form's address answers exactly as an address that is no form
//   at all, as does another patient's form; the same statements run for the three. Someone given sensitive items sees
//   the link whether or not the record has forms, so the link says nothing of them either. A copy of the record says it
//   never holds pre-visit forms, for every record alike. Signed out, the portal's forms send to sign-in.
// Type: Non-functional (security)
// Requirements: NFR-06, proposed FR-59, FR-19
// Use case: UC 22, Answer a pre-visit form
// Preconditions: Two patients with the same name, each with a portal account, have each given the same account
//   ("Synthetic Watcher") access to their record without sensitive items, and the second has given another account
//   ("Synthetic Reader") access with sensitive items. The first has a PHQ-9 asked for and answered, a GAD-7 open, and a
//   PHQ-9 before an appointment ten days ahead; the second has no forms. Another patient has a form.
// Inputs and system conditions: GET /portal, /portal/forms, /portal/record and /portal/forms/{form} as the proxy on each
//   record; POST /portal/forms/{form}; PhiReader.PortalFormAsync, the reads those handlers make, with every statement
//   captured; signed out, GET /portal/forms.
// Test data: Clinician "Dr. Forms Six".
// Steps:
//   1. As the proxy, on each record, open the home page, Pre-visit forms and Download my record. As the proxy with
//      sensitive items, open the second record's home page.
//   2. As the proxy on the first record, open and send to: its answered PHQ-9, a made-up form, the other patient's form.
//   3. Read the first, the made-up and the other patient's form as the reader does, counting statements.
//   4. Signed out, open Pre-visit forms.
// Expected result: Step 1: no "Pre-visit forms" link on either home page, but there for the proxy with sensitive items,
//   with nothing to fill in; Pre-visit forms and Download my record are the
//   same on both records, but for the antiforgery token, and the download page says "Neither includes the care team's
//   notes, messages, pre-visit forms, or who has looked at the record." Step 2: every one is 404 with the same body.
//   Step 3: nothing is returned, and the same number of statements, each the same, is sent for the three, the forms
//   read asking for the record's own forms by its patient. Step 4: sent to the portal's sign-in.
// Creator: Tahseen Arnan
public class TC_UC22_06_Nfr06_NoHintForAProxy(WebApps apps) : IClassFixture<WebApps>
{
    // The page without what differs from one request to the next: the antiforgery token.
    static string Steady(string html) => Regex.Replace(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"[^\"]+\"", "token");

    [Fact]
    public async Task A_proxy_without_sensitive_items_finds_no_trace_of_a_records_forms()
    {
        var visit = await VisitAsync(apps, "Dr. Forms Six");
        var later = await BookAsync(apps, visit, days: 10, time: "16:00");
        foreach (var (appointment, code) in new[] { (visit.Appointment, Phq9), (visit.Appointment, Gad7), (later, Phq9) })
        {
            using var asked = await AskAsync(visit.Session.Client, visit.Patient, appointment, code);
        }

        var answered = await RequestAsync(apps, visit.Appointment, Phq9);
        await AnswerAsync(apps, visit.Account, answered, Answers(PhqItems, 1, 1, 0, 0, 0, 0, 0, 0, 0));
        var other = await VisitAsync(apps, "Dr. Forms Other");
        using (var asked = await AskAsync(other.Session.Client, other.Patient, other.Appointment, Phq9))
        {
            Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
        }

        var theirs = await RequestAsync(apps, other.Appointment, Phq9);

        // A second record of the same name, with no forms; one proxy for both, without sensitive items.
        var empty = await apps.InsertPatientAsync("Synthetic", "Booked");
        var (emptyEmail, _) = await apps.CreatePatientAccountAsync(empty);
        var emptyAccount = await ProxyTests.AccountIdAsync(apps, emptyEmail);
        var proxy = await ProxyTests.PersonAsync(apps, "Synthetic", "Watcher");
        var onForms = await GrantAsync(apps, visit.Account, visit.Patient, proxy.Email, sensitive: false);
        var onEmpty = await GrantAsync(apps, emptyAccount, empty, proxy.Email, sensitive: false);

        async Task<(string Home, string Forms, string Record)> PagesAsync(Guid grant)
        {
            await ProxyTests.ViewAsync(proxy, grant);
            return (await proxy.Client.GetStringAsync("/portal", Cancel), await proxy.Client.GetStringAsync("/portal/forms", Cancel),
                await proxy.Client.GetStringAsync("/portal/record", Cancel));
        }

        var withForms = await PagesAsync(onForms);
        var without = await PagesAsync(onEmpty);
        Assert.DoesNotContain("Pre-visit forms", Text(withForms.Home));
        Assert.DoesNotContain("data-forms", withForms.Home);
        Assert.DoesNotContain("data-forms", without.Home);

        // With sensitive items, the link is there on a record that has no forms at all.
        var reader = await ProxyTests.PersonAsync(apps, "Synthetic", "Reader");
        await ProxyTests.ViewAsync(reader, await GrantAsync(apps, emptyAccount, empty, reader.Email, sensitive: true));
        var readerHome = Text(await reader.Client.GetStringAsync("/portal", Cancel));
        Assert.Contains("Pre-visit forms", readerHome);
        Assert.DoesNotContain("to fill in", readerHome);
        Assert.Equal(Steady(without.Forms), Steady(withForms.Forms));
        Assert.Equal(Steady(without.Record), Steady(withForms.Record));
        Assert.Contains("Neither includes the care team's notes, messages, pre-visit forms, or who has looked at the record.", Text(withForms.Record));

        // On the record with forms: its own answered form, a made-up one and another patient's are all the same nothing.
        await ProxyTests.ViewAsync(proxy, onForms);
        var answers = Answers(PhqItems, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var bodies = new List<string>();
        foreach (var form in new[] { answered, Guid.CreateVersion7(), theirs })
        {
            using var look = await proxy.Client.GetAsync(FormPath(form), Cancel);
            Assert.Equal(HttpStatusCode.NotFound, look.StatusCode);
            bodies.Add(Steady(await look.Content.ReadAsStringAsync(Cancel)));
            using var send = await SendAsync(proxy.Client, form, answers);
            Assert.Equal(HttpStatusCode.NotFound, send.StatusCode);
            bodies.Add(Steady(await send.Content.ReadAsStringAsync(Cancel)));
        }

        Assert.Single(bodies.Distinct());

        // The reads behind those answers send the same statements for the three, whoever's they are.
        var sent = new List<List<string>>();
        foreach (var (user, grant, form) in new[] { (proxy.Account, (Guid?)onForms, answered), (proxy.Account, onForms, Guid.CreateVersion7()), (proxy.Account, onForms, theirs) })
        {
            sent.Add(await StatementsAsync(user, grant, visit.Patient, form));
        }

        Assert.All(sent, statements => Assert.Equal(sent[0], statements));
        Assert.NotEmpty(sent[0]);
        // The forms read every forms page shares (PhiReader.FormEntriesAsync) asks for the record's own forms by name,
        // whatever row-level security would also do: a clinician reads every patient's.
        Assert.Contains(sent[0], statement => statement.Contains("questionnaire_requests", StringComparison.Ordinal)
            && Regex.IsMatch(statement, "\\.patient_id = @patientId\\b"));

        // Signed out: the portal's sign-in, for the list and for a form alike.
        using var signedOut = await Client(apps.OnPrem, new System.Net.CookieContainer()).GetAsync("/portal/forms", Cancel);
        Assert.Equal("/portal/sign-in", PathOf(signedOut));
    }

    // PhiReader.PortalFormAsync as the session would read it, with each statement's text captured (its parameters' values
    // aside): one transaction, the user context set first, as the page's.
    async Task<List<string>> StatementsAsync(Guid user, Guid? grant, Guid patient, Guid form)
    {
        var context = new UserContext { UserId = user, ActingGrant = grant };
        var commands = new PatientSearchQueryTests.CapturedCommands();
        var options = new DbContextOptionsBuilder<EhrDbContext>();
        EhrDbContext.Configure(options, apps.Database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword));
        options.AddInterceptors(new UserContextInterceptor(context), commands);
        await using var db = new EhrDbContext(options.Options);
        var reader = new PhiReader(db, new AuditRecorder(db, context, new AuditSource(AuditSources.OnPrem)), context);
        await using var transaction = await db.Database.BeginTransactionAsync(Cancel);
        Assert.Null(await reader.PortalFormAsync(patient, form, Cancel));
        await transaction.RollbackAsync(Cancel);
        return [.. commands.Executed.Select(command => command.Text)];
    }
}
