using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RecordViewTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-42
// Purpose / scenario: The patient sees each view made by someone they share their record with, named by the address
//   they gave access to, exactly as their Access page shows it, and nothing else about that person's account; a view
//   made before access was removed stays listed.
// Type: Functional (alternative flow: proxies' views)
// Requirements: proposed FR-57, FR-19
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a confirmed portal account, signed in on the on-site app. Two other patients of the
//   clinic have portal accounts of their own.
// Inputs and system conditions: The patient gives each of the two access; each opens the record, the first also its
//   Who viewed page; the patient removes the second's access; the patient GETs /portal/views; on the on-site app.
// Test data: The two portal accounts' addresses; their names, "Synthetic Proxyone" and "Synthetic Proxytwo".
// Steps:
//   1. The first proxy opens the patient's record, then Who viewed the record.
//   2. The second proxy opens the patient's record. The patient removes the second's access.
//   3. The patient opens Who viewed my record, and their Access page.
// Expected result: Of the page's lines, those of people the record is shared with are, newest first: the second's
//   address, "Home page"; the first's, "Who viewed the record"; the first's, "Home page". Each address is the one the
//   Access page shows. Neither proxy's name is on the page.
// Creator: Tahseen Arnan
public class TC_UC6_42_ProxyViewsShownToThePatient(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task The_patient_sees_each_proxys_views_by_the_address_they_gave_access_to()
    {
        var (_, _, patient) = await ViewedPatientAsync(apps, "Sharing");

        // Step 1.
        var (first, _) = await VisitSummaryTests.ProxyForAsync(apps, patient, "Proxyone", sensitive: true);
        Assert.Contains("Who viewed", Words(await first.Client.GetStringAsync(ViewsPath, Cancel)));

        // Step 2.
        var (second, grant) = await VisitSummaryTests.ProxyForAsync(apps, patient, "Proxytwo", sensitive: false);
        using (var removed = await PostFormAsync(patient.Client, "/portal/access?handler=Revoke", "/portal/access", ("grantId", grant)))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, removed.StatusCode);
        }

        // Step 3.
        var html = await PageAsync(patient.Client);
        Assert.Equal(
            [
                $"proxy | {second.Email} | Home page",
                $"proxy | {first.Email} | Who viewed the record",
                $"proxy | {first.Email} | Home page",
            ],
            Lines(html).Where(line => line.StartsWith("proxy", StringComparison.Ordinal)).Select(line => string.Join(" | ", line.Split(" | ").Take(3))));
        var access = await patient.Client.GetStringAsync("/portal/access", Cancel);
        Assert.Contains(first.Email, access);
        Assert.Contains(second.Email, access);
        Assert.DoesNotContain("Proxyone", html);
        Assert.DoesNotContain("Proxytwo", html);
    }
}
