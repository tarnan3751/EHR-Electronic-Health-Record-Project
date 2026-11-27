using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.ProxyTests;

namespace Ehr.Web.Tests.TestCases.UC18;

// Test Case ID: TC-UC18-07
// Purpose / scenario: Someone who isn't a patient of the clinic becomes a patient's proxy. The patient gives access
//   to their email address; the email's link creates a portal account with no record of its own; that account sees
//   only Access and the record shared with it, and writes to the care team as the patient's proxy, by name.
// Type: Functional (alternative flow: the proxy has no portal account)
// Requirements: FR-19, FR-18, proposed FR-44, proposed FR-46
// Use case: UC 18, Give someone proxy access to a record
// Preconditions: The patient has a portal account and is on one clinician's panel. Nobody has a portal account with
//   the carer's address.
// Inputs and system conditions: POST /portal/access as the patient; GET and POST /portal/invitation with the emailed
//   code, signed out; POST /portal/sign-in; GET /portal, /portal/choose, /portal/access and /portal/results; POST
//   /portal/acting and /portal/messages/new; on the on-site app.
// Test data: Patient "Synthetic Cared". The carer's name "Synthetic Outside Carer". Scope with messages.
// Steps:
//   1. As the patient, give access to the carer's address.
//   2. Signed out, open the link from the email, enter a name and a password twice, and choose "Create account".
//   3. Sign in as the carer. Request /portal and /portal/results.
//   4. Choose "View this record". Open "Results" and "Sharing".
//   5. Send a message to the care team. As the clinician, open the conversation.
// Expected result: Step 1: one email to the carer's address with a button "Create your portal account" to
//   /portal/invitation with a code, and nobody's name. Step 2: the page says "Account created."; the account has no patient,
//   a confirmed address and the name typed; the creation is in the audit log under the new account. Step 3: sign-in
//   lands on "Whose record?", which lists "Synthetic Cared" and no "My record"; the app bar has Access and no record
//   links; /portal and /portal/results both lead back to "Whose record?". Step 4: the patient's record, with the band
//   and "Choose another record"; Results opens; Access lists the record and has no "Who can see your record". Step 5:
//   the clinician sees the message from "Synthetic Outside Carer (proxy)".
// Creator: Tahseen Arnan
public class TC_UC18_07_ProxyOnlyAccount(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task An_invited_carer_creates_an_account_that_sees_only_the_record_shared_with_it()
    {
        var patient = await PersonAsync(apps, "Synthetic", "Cared");
        var clinician = await apps.CreateStaffAsync("Dr. Sees Proxy", StaffRoles.Clinician);
        await apps.AssignAsync(clinician, patient.Patient);
        var carer = $"synthetic-carer-{Guid.NewGuid():N}@example.com";

        Assert.Contains("Shared.", await GiveAsync(patient, carer, "record-and-messages"));
        var email = apps.Emails.Sent.Last();
        Assert.Equal(carer, email.To, ignoreCase: true);
        Assert.Equal("Create your portal account", apps.Emails.Actions[email.Body].Label);
        Assert.DoesNotContain("Cared", email.Subject + email.Body, StringComparison.OrdinalIgnoreCase);
        var invitation = InvitationPath(apps, carer);

        var form = await Client(apps.OnPrem, new System.Net.CookieContainer()).GetStringAsync(invitation, Cancel);
        Assert.Contains("name=\"Input.Name\"", form);
        Assert.DoesNotContain("Cared", form);
        Assert.Contains("Account created.", await AcceptAsync(apps, invitation, "Synthetic Outside Carer"));
        var account = await AccountIdAsync(apps, carer);
        Assert.Equal(["none true Synthetic Outside Carer"], await QueryAsync(apps,
            $"SELECT coalesce(patient_id::text, 'none') || ' ' || email_confirmed || ' ' || display_name FROM ehr.patient_users WHERE id = '{account}' AND @patient IS NOT NULL", patient.Patient));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.ProxyAccountCreated, account));

        var session = await SignInAsync(apps, carer);
        Assert.Equal("/portal/choose", session.Path);
        Assert.Contains("<h1>Whose record?</h1>", session.Page);
        Assert.Contains("<h2>Synthetic Cared</h2>", session.Page);
        Assert.DoesNotContain("data-record=\"own\"", session.Page);
        Assert.Contains("href=\"/portal/access\"", session.Page);
        Assert.DoesNotContain("href=\"/portal/results\"", session.Page);
        Assert.DoesNotContain("href=\"/portal\"", session.Page);
        foreach (var path in new[] { "/portal", "/portal/results", "/portal/appointments", "/portal/messages" })
        {
            using var sent = await session.Client.GetAsync(path, Cancel);
            Assert.Equal("/portal/choose", PathOf(sent));
        }

        var grant = await GrantIdAsync(apps, patient.Patient);
        using var chosen = await PostFormAsync(session.Client, "/portal/acting", "/portal/choose", ("grantId", grant));
        var home = await ReadAsync(chosen);
        Assert.Equal("/portal", PathOf(chosen));
        Assert.Contains("Synthetic Cared&#x27;s record", home);
        Assert.Contains("Choose another record", home);
        Assert.Contains("href=\"/portal/results\"", home);
        Assert.Contains("Test results", await session.Client.GetStringAsync("/portal/results", Cancel));
        var access = await session.Client.GetStringAsync("/portal/access", Cancel);
        Assert.Contains("Records shared with you", access);
        Assert.DoesNotContain("Who can see your record", access);
        Assert.DoesNotContain("name=\"Email\"", access);

        var thread = await MessageTests.StartAsync(session.Client, "Synthetic: from a carer", "Synthetic: asking for my neighbour");
        var staff = await SignInAsync(apps.OnPrem, clinician);
        var conversation = await staff.Client.GetStringAsync($"/patients/{patient.Patient}/messages/{thread}", Cancel);
        Assert.Contains("<strong>Synthetic Outside Carer (proxy)</strong>", conversation);
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Read, account, patient.Patient, AuditSources.OnPrem) > 0 ? 1 : 0);
    }
}
