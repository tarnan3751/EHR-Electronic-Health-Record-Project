using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.ProxyTests;

namespace Ehr.Web.Tests.TestCases.UC18;

// Test Case ID: TC-UC18-01
// Purpose / scenario: A patient gives a family member access to their record from the portal, lets them see sensitive
//   items, and later removes the access. The family member is told by an email that names nobody.
// Type: Functional (successful flow)
// Requirements: FR-19, proposed FR-44
// Use case: UC 18, Give someone proxy access to a record
// Preconditions: The patient and the family member each have a portal account. The patient is signed in on the
//   on-site app.
// Inputs and system conditions: GET and POST /portal/access; POST /portal/access?handler=Sensitive and
//   ?handler=Revoke; on the on-site app.
// Test data: Patient "Synthetic Grantor". Scope "Record, results, appointments and messages". For 30 days.
// Steps:
//   1. Open "Access". Enter the family member's email, choose the scope and "30 days", and choose "Give access".
//   2. Choose "Show sensitive items" for that person.
//   3. Choose "Remove access".
// Expected result: Step 1: the page says "Saved:" and lists the email under "Who can view your record" with the scope,
//   an end date, "Given by you." and "They can't see items your care team marked sensitive."; one email goes to that
//   address, subject "You have been given access to a record", with a button to /portal/access and neither person's
//   name in it. Step 2: "Saved:", and the entry now says they can also see sensitive items. Step 3: "Removed:"; the
//   entry moves to "Access that has ended"; "Nobody else can view your record."
// Creator: Tahseen Arnan
public class TC_UC18_01_SuccessfulFlow(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_patient_gives_changes_and_removes_a_family_members_access()
    {
        var patient = await PersonAsync(apps, "Synthetic", "Grantor");
        var proxy = await PersonAsync(apps, "Synthetic", "Relative");
        var empty = await patient.Client.GetStringAsync("/portal/access", Cancel);
        Assert.Contains("Nobody else can view your record.", empty);
        Assert.Contains("Nobody has given you access to their record.", empty);
        Assert.Contains("href=\"/portal/access\"", empty);

        var emails = apps.Emails.Sent.Count;
        var given = await GiveAsync(patient, proxy.Email.ToUpperInvariant(), "record-and-messages", days: 30);
        Assert.Contains("Saved:", given);
        Assert.Contains($"<h3>{proxy.Email}</h3>", given);
        Assert.Contains("Record, results, appointments and messages. Until ", Words(given));
        Assert.Contains("Given by you.", given);
        Assert.Contains("They can't see items your care team marked sensitive.", Words(given));
        Assert.Equal([$"{proxy.Email} record-and-messages false patient in-force"], await GrantsAsync(apps, patient.Patient));
        Assert.Equal(emails + 1, apps.Emails.Sent.Count);
        var email = apps.Emails.Sent.Last();
        Assert.Equal(proxy.Email.ToUpperInvariant(), email.To, ignoreCase: true);
        Assert.Equal("You have been given access to a record", email.Subject);
        Assert.EndsWith("/portal/access", apps.Emails.Actions[email.Body].Link);
        foreach (var name in new[] { "Grantor", "Relative", patient.Email })
        {
            Assert.DoesNotContain(name, email.Subject + email.Body, StringComparison.OrdinalIgnoreCase);
        }

        var grant = await GrantIdAsync(apps, patient.Patient);
        using var allowed = await PostFormAsync(patient.Client, "/portal/access?handler=Sensitive", "/portal/access", ("grantId", grant), ("allow", "true"));
        var shown = await ReadAsync(allowed);
        Assert.Contains("Saved:", shown);
        Assert.Contains("They can also see items your care team marked sensitive.", shown);
        Assert.Equal([$"{proxy.Email} record-and-messages true patient in-force"], await GrantsAsync(apps, patient.Patient));

        using var removed = await PostFormAsync(patient.Client, "/portal/access?handler=Revoke", "/portal/access", ("grantId", grant));
        var after = await ReadAsync(removed);
        Assert.Contains("Removed:", after);
        Assert.Contains("Nobody else can view your record.", after);
        Assert.Contains("Access that has ended", after);
        Assert.Equal([$"{proxy.Email} record-and-messages true patient revoked"], await GrantsAsync(apps, patient.Patient));
        Assert.Contains("Nobody has given you access to their record.", await proxy.Client.GetStringAsync("/portal/access", Cancel));
    }
}
