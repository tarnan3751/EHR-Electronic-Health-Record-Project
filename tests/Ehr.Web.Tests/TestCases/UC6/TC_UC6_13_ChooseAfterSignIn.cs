using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.ProxyTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-13
// Purpose / scenario: After signing in, an account that has records shared with it chooses whose record to view, its
//   own first; an account with nothing shared goes straight to its own record. Coming back to the choice always
//   starts from the account itself, whatever the browser was looking at.
// Type: Functional (alternative flow: step 1 alternative, a proxy chooses which patient's records to view)
// Requirements: FR-19, FR-16, proposed FR-46
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: Two patients with portal accounts. The first has given the second access to their record.
// Inputs and system conditions: POST /portal/sign-in; GET /portal/choose and /portal; POST /portal/acting and
//   /portal/acting?handler=Mine; on the on-site app.
// Test data: Patients "Synthetic Sharer" and "Synthetic Chooser".
// Steps:
//   1. Sign in as the first patient, who has nothing shared with them.
//   2. Sign in as the second patient.
//   3. Choose "View my record".
//   4. Open /portal/choose again, choose the first patient's record, then open /portal/choose once more and request
//      /portal.
// Expected result: Step 1: lands on "My record". Step 2: lands on "Whose record?", which lists "My record" first and
//   then "Synthetic Sharer", shared, with what it covers and until when. Step 3: the second patient's own record, with
//   no band. Step 4: the first patient's record with the band; after opening the choice again, /portal is the second
//   patient's own record with no band.
// Creator: Tahseen Arnan
public class TC_UC6_13_ChooseAfterSignIn(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task After_sign_in_an_account_with_shared_records_chooses_whose_to_view()
    {
        var sharer = await PersonAsync(apps, "Synthetic", "Sharer");
        var chooser = await PersonAsync(apps, "Synthetic", "Chooser");
        await GiveAsync(sharer, chooser.Email);
        var grant = await GrantIdAsync(apps, sharer.Patient);

        var alone = await SignInAsync(apps, sharer.Email);
        Assert.Equal("/portal", alone.Path);
        Assert.Contains("<h1>My record</h1>", alone.Page);

        var session = await SignInAsync(apps, chooser.Email);
        Assert.Equal("/portal/choose", session.Path);
        var mine = session.Page.IndexOf("data-record=\"own\"", StringComparison.Ordinal);
        var shared = session.Page.IndexOf("<h2>Synthetic Sharer</h2>", StringComparison.Ordinal);
        Assert.True(mine > 0 && shared > mine, "My record is listed first, then the record shared with the account.");
        Assert.Contains("Shared with you. Record, results and appointments. Until ", Words(session.Page));

        using var own = await PostFormAsync(session.Client, "/portal/acting?handler=Mine", "/portal/choose");
        var record = await ReadAsync(own);
        Assert.Equal("/portal", PathOf(own));
        Assert.Contains("<dd>Synthetic Chooser</dd>", record);
        Assert.DoesNotContain("data-acting", record);

        using var other = await PostFormAsync(session.Client, "/portal/acting", "/portal/choose", ("grantId", grant));
        var theirs = await ReadAsync(other);
        Assert.Contains("<dd>Synthetic Sharer</dd>", theirs);
        Assert.Contains("data-acting", theirs);
        Assert.Contains("Back to my record", theirs);

        Assert.Contains("<h1>Whose record?</h1>", await session.Client.GetStringAsync("/portal/choose", Cancel));
        var back = await session.Client.GetStringAsync("/portal", Cancel);
        Assert.Contains("<dd>Synthetic Chooser</dd>", back);
        Assert.DoesNotContain("data-acting", back);
    }
}
