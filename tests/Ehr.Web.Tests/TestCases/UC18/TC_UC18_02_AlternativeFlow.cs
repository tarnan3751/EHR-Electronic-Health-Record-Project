using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.ProxyTests;

namespace Ehr.Web.Tests.TestCases.UC18;

// Test Case ID: TC-UC18-02
// Purpose / scenario: The clinic records a proxy for a patient, from the chart. The proxy then writes to the care team
//   for the patient, and the clinician sees who wrote. A clinician under emergency access can see who the proxies
//   are but can't record or remove one. Only the patient can let the proxy see sensitive items.
// Type: Functional (alternative flow)
// Requirements: FR-19, FR-18, proposed FR-44
// Use case: UC 18, Give someone proxy access to a record
// Preconditions: A clinician has the patient on their panel; a second clinician doesn't. The patient and the proxy
//   have portal accounts.
// Inputs and system conditions: GET /patients/{id}; GET and POST /patients/{id}/proxies and ?handler=Revoke; as the
//   proxy, POST /portal/acting and /portal/messages/new; on the on-site app.
// Test data: Clinicians "Dr. Records Proxy" and "Dr. Covering Proxy". Patient "Synthetic Minor", proxy "Synthetic
//   Guardian". Scope with messages, for 1 year. Subject "Synthetic: from a guardian".
// Steps:
//   1. As the first clinician, follow the chart's "People who can see this record", enter the proxy's email,
//      choose the scope and "1 year", and choose "Give access".
//   2. As the proxy, choose the patient's record and send a message to the care team.
//   3. As the first clinician, open the conversation.
//   4. As the second clinician, after giving a reason on the chart, open the proxies page and post a removal.
//   5. As the patient, open "Sharing". As the first clinician, choose "Remove access".
// Expected result: Step 1: "Access given.", and the table lists the email, the scope, "Hidden" for sensitive items, "By the
//   clinic" and "In force"; the proxy is sent the same email as in TC-UC18-01. Step 2: the message is sent. Step 3: it
//   is shown from "Synthetic Guardian (proxy)", not from the patient. Step 4: the page lists the proxy, says "To give or
//   remove access, add this patient to My patients." with no form, and the post is redirected to the chart with
//   the proxy still in force; opening the page is a recorded read. Step 5: the patient sees "Shared by your clinic."
//   and can choose "Share sensitive items too"; after the clinician's removal the table says "Removed".
// Creator: Tahseen Arnan
public class TC_UC18_02_AlternativeFlow(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_clinic_records_a_proxy_who_writes_for_the_patient_and_only_the_care_team_changes_it()
    {
        var patient = await PersonAsync(apps, "Synthetic", "Minor");
        var proxy = await PersonAsync(apps, "Synthetic", "Guardian");
        var clinician = await apps.CreateStaffAsync("Dr. Records Proxy", StaffRoles.Clinician);
        await apps.AssignAsync(clinician, patient.Patient);
        var staff = await SignInAsync(apps.OnPrem, clinician);
        var page = $"/patients/{patient.Patient}/proxies";

        Assert.Matches($"<a [^>]*href=\"{page}\"[^>]*>People who can see this record</a>", await staff.Client.GetStringAsync($"/patients/{patient.Patient}", Cancel));
        Assert.Contains("No one else has access.", await staff.Client.GetStringAsync(page, Cancel));
        var emails = apps.Emails.Sent.Count;
        using var recorded = await PostFormAsync(staff.Client, page, page, ("Email", proxy.Email), ("Scope", "record-and-messages"), ("Days", 365));
        var table = await ReadAsync(recorded);
        Assert.Contains("Access given.", table);
        Assert.Contains($"<td>{proxy.Email}</td>", table);
        Assert.Contains("<td>Hidden</td>", table);
        Assert.Contains("By the clinic", table);
        Assert.Contains("In force", table);
        Assert.Equal([$"{proxy.Email} record-and-messages false staff in-force"], await GrantsAsync(apps, patient.Patient));
        Assert.Equal(emails + 1, apps.Emails.Sent.Count);
        Assert.Equal("Someone shared their record with you", apps.Emails.Sent.Last().Subject);

        var grant = await GrantIdAsync(apps, patient.Patient);
        await ViewAsync(proxy, grant);
        var thread = await MessageTests.StartAsync(proxy.Client, "Synthetic: from a guardian", "Synthetic: asking for my child");
        var conversation = await staff.Client.GetStringAsync($"/patients/{patient.Patient}/messages/{thread}", Cancel);
        Assert.Contains("<strong>Synthetic Guardian (proxy)</strong>", conversation);
        Assert.DoesNotContain("<strong>Synthetic Minor</strong>", conversation);
        Assert.Contains("<strong>You</strong>", await proxy.Client.GetStringAsync($"/portal/messages/{thread}", Cancel));
        Assert.Contains("<strong>A proxy</strong>", await patient.Client.GetStringAsync($"/portal/messages/{thread}", Cancel));

        var covering = await apps.CreateStaffAsync("Dr. Covering Proxy", StaffRoles.Clinician);
        var other = await SignInAsync(apps.OnPrem, covering);
        using var reason = await PostFormAsync(other.Client, $"/patients/{patient.Patient}/reason", $"/patients/{patient.Patient}/reason", ("Reason", "Covering the night shift"));
        var reads = await apps.AuditEventsAsync(AuditActions.Read, covering.Id, patient.Patient);
        var seen = await other.Client.GetStringAsync(page, Cancel);
        Assert.Contains(proxy.Email, seen);
        Assert.Contains("To give or remove access, add this patient to My patients.", seen);
        Assert.DoesNotContain("name=\"Email\"", seen);
        Assert.DoesNotContain("handler=Revoke", seen);
        Assert.Equal(reads + 1, await apps.AuditEventsAsync(AuditActions.Read, covering.Id, patient.Patient));
        using var notTheirs = await Client(apps.OnPrem, other.Jar, followRedirects: false)
            .PostAsync($"{page}?handler=Revoke", Form(("__RequestVerificationToken", Token(seen)), ("grantId", grant.ToString())), Cancel);
        Assert.Equal($"/patients/{patient.Patient}", new Uri(Base, notTheirs.Headers.Location!).AbsolutePath);
        Assert.EndsWith("in-force", (await GrantsAsync(apps, patient.Patient)).Single());

        var own = await patient.Client.GetStringAsync("/portal/access", Cancel);
        Assert.Contains("Shared by your clinic.", own);
        Assert.Contains("Share sensitive items too", own);
        using var removed = await PostFormAsync(staff.Client, $"{page}?handler=Revoke", page, ("grantId", grant));
        var after = await ReadAsync(removed);
        Assert.Contains("Access removed.", after);
        Assert.Contains("<span class=\"sc-tag\">Removed</span>", after);
        Assert.EndsWith("revoked", (await GrantsAsync(apps, patient.Patient)).Single());
    }
}
