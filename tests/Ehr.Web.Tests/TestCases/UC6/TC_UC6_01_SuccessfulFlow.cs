using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.PortalResultsTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-01
// Purpose / scenario: A signed-in patient opens their test results and sees their own released results, newest first,
//   each with its value, normal range, flag and plain-language meaning, and nobody else's results.
// Type: Functional (successful flow)
// Requirements: FR-16, FR-32
// Use case: UC 6, View test results in the portal
// Preconditions: The patient has a confirmed portal account and is signed in on the on-site app. The clinic's server
//   is up.
// Inputs and system conditions: GET /portal/results, then GET /portal, on the on-site app (ehr_app role).
// Test data: Patient Jordan Results with LDL cholesterol (LOINC 13457-7) 90 mg/dL, range below 100 mg/dL, sampled and
//   released Oct 14, 2026 15:00 UTC; Hemoglobin A1c (4548-4) 6.9 %, range 4.0 to 5.6 %, sampled and released
//   Nov 2, 2026 15:00 UTC, care-team note "We'll talk about this at your next visit."; Glucose (2345-7) 140 mg/dL,
//   range 70 to 99, sampled Nov 2, 2026, not released; Potassium (2823-3) 4.1 mmol/L, range 3.5 to 5.1, released but
//   status "preliminary". Another patient, Riley Elsewhere, with Thyroid stimulating hormone (3016-3) 2.1 mIU/L,
//   range 0.4 to 4.0, released Nov 2, 2026.
// Steps:
//   1. Sign in to the portal as Jordan Results.
//   2. Open /portal/results.
//   3. Open /portal.
// Expected result: The results page sends Cache-Control: no-store and lists Hemoglobin A1c before LDL cholesterol,
//   dated "Nov 2, 2026 UTC". Hemoglobin A1c shows "6.9 %", "4 to 5.6 %", the "High" flag, "Your result is above the
//   normal range (4 to 5.6 %).", "This test measures your average blood sugar over the past 2 to 3 months." and the
//   note "We'll talk about this at your next visit."; LDL cholesterol shows "Your result is in the normal range
//   (below 100 mg/dL).". Glucose, Potassium and Thyroid stimulating hormone don't appear. Exactly one read of the
//   patient is recorded in the audit log. The portal home also sends Cache-Control: no-store.
// Creator: Simar Rekhi
public class TC_UC6_01_SuccessfulFlow(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_patient_sees_their_released_results_newest_first_in_plain_language()
    {
        var own = await apps.InsertPatientAsync("Jordan", "Results");
        var other = await apps.InsertPatientAsync("Riley", "Elsewhere");
        await apps.InsertResultAsync(own, "LDL cholesterol", "13457-7", 90m, "mg/dL", null, 100m, Oct14, Oct14);
        await apps.InsertResultAsync(own, "Hemoglobin A1c", "4548-4", 6.9m, "%", 4.0m, 5.6m, Nov2, Nov2,
            note: "We'll talk about this at your next visit.");
        await apps.InsertResultAsync(own, "Glucose", "2345-7", 140m, "mg/dL", 70m, 99m, Nov2, issuedAt: null);
        await apps.InsertResultAsync(own, "Potassium", "2823-3", 4.1m, "mmol/L", 3.5m, 5.1m, Nov2, Nov2, status: "preliminary");
        await apps.InsertResultAsync(other, "Thyroid stimulating hormone", "3016-3", 2.1m, "mIU/L", 0.4m, 4.0m, Nov2, Nov2);
        var (client, _) = await SignInPatientAsync(apps, apps.OnPrem, own);
        // Signing in opens the portal's home, itself a read of the patient.
        var readsBefore = await apps.AuditEventsAsync(AuditActions.Read, patientId: own);

        using var response = await client.GetAsync("/portal/results", Cancel);
        var html = await ReadAsync(response);

        // Not kept by the browser, so Back after signing out doesn't show it again.
        Assert.True(response.Headers.CacheControl?.NoStore);
        // The sample's date, labelled with its time zone.
        Assert.Contains("Nov 2, 2026 UTC", html);

        // Newest first.
        Assert.True(html.IndexOf("Hemoglobin A1c", StringComparison.Ordinal) < html.IndexOf("LDL cholesterol", StringComparison.Ordinal));
        Assert.Contains("6.9 %", html);
        Assert.Contains("4 to 5.6 %", html);
        Assert.Contains("Your result is above the normal range (4 to 5.6 %).", html);
        Assert.Contains("This test measures your average blood sugar over the past 2 to 3 months.", html);
        Assert.Contains("We&#x27;ll talk about this at your next visit.", html);
        Assert.Contains(">High<", html);
        Assert.Contains("Your result is in the normal range (below 100 mg/dL).", html);
        // Not released yet, not finished, or someone else's.
        Assert.DoesNotContain("Glucose", html);
        Assert.DoesNotContain("Potassium", html);
        Assert.DoesNotContain("Thyroid", html);
        // One view of one patient: one recorded read.
        Assert.Equal(readsBefore + 1, await apps.AuditEventsAsync(AuditActions.Read, patientId: own));

        // Nor the portal's home, which shows the record too.
        using var home = await client.GetAsync("/portal", Cancel);
        Assert.True(home.Headers.CacheControl?.NoStore);
    }
}
