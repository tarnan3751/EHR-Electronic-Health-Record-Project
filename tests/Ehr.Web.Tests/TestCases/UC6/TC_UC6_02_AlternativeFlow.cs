using Xunit;
using static Ehr.Web.Tests.PortalResultsTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-02
// Purpose / scenario: A signed-in patient with no released results opens their test results and is told they have
//   none yet, and pointed to their care team; a result that isn't released doesn't appear.
// Type: Functional (alternative flow)
// Requirements: FR-16
// Use case: UC 6, View test results in the portal
// Preconditions: The patient has a confirmed portal account and is signed in on the on-site app. The clinic's server
//   is up.
// Inputs and system conditions: GET /portal/results on the on-site app (ehr_app role).
// Test data: Patient Morgan Waiting with one result that isn't released: Lipid panel (LOINC 2093-3) 180 mg/dL, range
//   below 200 mg/dL, sampled Nov 2, 2026 15:00 UTC, no release time.
// Steps:
//   1. Sign in to the portal as Morgan Waiting.
//   2. Open /portal/results.
// Expected result: The page says "No test results yet."
//   and doesn't show "Lipid panel".
// Creator: Simar Rekhi
public class TC_UC6_02_AlternativeFlow(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_patient_with_no_released_results_is_told_so()
    {
        var patient = await apps.InsertPatientAsync("Morgan", "Waiting");
        await apps.InsertResultAsync(patient, "Lipid panel", "2093-3", 180m, "mg/dL", null, 200m, Nov2, issuedAt: null);
        var (client, _) = await SignInPatientAsync(apps, apps.OnPrem, patient);

        var html = await client.GetStringAsync("/portal/results", Cancel);

        Assert.Contains("No test results yet.", html);
        Assert.DoesNotContain("Lipid panel", html);
    }
}
