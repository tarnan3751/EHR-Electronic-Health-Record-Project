using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.ProxyTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-10
// Purpose / scenario: A proxy chooses whose record to view. Someone a patient gave access to signs in with their own
//   account, picks the patient's record, sees that patient's results and not their own, with a band on every page
//   saying whose record it is, and goes back to their own. What they read is recorded under their account and the
//   patient.
// Type: Functional (alternative flow: step 1 alternative, a proxy chooses which patient's records to view)
// Requirements: FR-19, FR-16, FR-30
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: Two patients with portal accounts, each with one released result. The first has given the second
//   access to their record. Both are signed in on the on-site app.
// Inputs and system conditions: GET /portal/access; POST /portal/acting with the grant's ID; GET /portal and
//   /portal/results; POST /portal/acting?handler=Mine.
// Test data: Patient "Synthetic Parent" with result "Hemoglobin A1c"; proxy "Synthetic Carer" with result "Ferritin".
// Steps:
//   1. As the proxy, open "Access".
//   2. Choose "View this record".
//   3. Open "Results".
//   4. Choose "Back to my record", and open "Results".
// Expected result: Step 1: "Records you can view" lists "Synthetic Parent". Step 2: the home page is headed "Synthetic
//   Parent's record", shows that patient's name and record number, and the band "Shared record. You're viewing Synthetic
//   Parent's record, not your own." Step 3: the patient's result and not the proxy's; the band again; the read is
//   recorded with the proxy's account as the actor and the patient as the subject. Step 4: the proxy's own record and
//   result, no band, and nothing of the patient's.
// Creator: Tahseen Arnan
public class TC_UC6_10_ProxyChoosesRecord(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_proxy_chooses_a_record_sees_only_it_and_goes_back_to_their_own()
    {
        var patient = await PersonAsync(apps, "Synthetic", "Parent");
        var proxy = await PersonAsync(apps, "Synthetic", "Carer");
        var when = new DateTimeOffset(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);
        await apps.InsertResultAsync(patient.Patient, "Hemoglobin A1c", null, 6.1m, "%", 4m, 5.6m, when, when);
        await apps.InsertResultAsync(proxy.Patient, "Ferritin", null, 80m, "ng/mL", 30m, 400m, when, when);
        await GiveAsync(patient, proxy.Email);
        var grant = await GrantIdAsync(apps, patient.Patient);

        var access = await proxy.Client.GetStringAsync("/portal/access", Cancel);
        Assert.Contains("<h3>Synthetic Parent</h3>", access);
        Assert.Contains($"name=\"grantId\" value=\"{grant}\"", access);
        Assert.DoesNotContain("data-acting", access);

        var home = await ViewAsync(proxy, grant);
        Assert.Contains("<h1>Synthetic Parent&#x27;s record</h1>", home);
        Assert.Contains("Shared record. You're viewing Synthetic Parent's record, not your own.", Words(home));
        Assert.Contains("<dd>Synthetic Parent</dd>", home);
        Assert.DoesNotContain("Synthetic Carer</dd>", home);

        var reads = await apps.AuditEventsAsync(AuditActions.Read, proxy.Account, patient.Patient);
        var results = await proxy.Client.GetStringAsync("/portal/results", Cancel);
        Assert.Contains("Hemoglobin A1c", results);
        Assert.DoesNotContain("Ferritin", results);
        Assert.Contains("data-acting", results);
        Assert.Equal(reads + 1, await apps.AuditEventsAsync(AuditActions.Read, proxy.Account, patient.Patient));
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.Read, patient.Account, patient.Patient, AuditSources.Cloud));

        using var back = await PostFormAsync(proxy.Client, "/portal/acting?handler=Mine", "/portal/results");
        var own = await ReadAsync(back);
        Assert.Contains("<h1>My record</h1>", own);
        Assert.Contains("<dd>Synthetic Carer</dd>", own);
        Assert.DoesNotContain("data-acting", own);
        var ownResults = await proxy.Client.GetStringAsync("/portal/results", Cancel);
        Assert.Contains("Ferritin", ownResults);
        Assert.DoesNotContain("Hemoglobin A1c", ownResults);
    }
}
