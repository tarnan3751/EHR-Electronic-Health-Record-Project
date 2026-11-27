using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-02
// Purpose / scenario: Open a chart that isn't on the clinician's panel. A clinician who opens the chart of a patient
//   not assigned to them doesn't see it: they are refused or asked for a reason first.
// Type: Functional (alternative flow)
// Requirements: FR-02, FR-30
// Use case: UC 4, View a patient's chart
// Preconditions: The clinician is signed in on the on-site app, with password and authenticator code. The patient is
//   not on their panel, and they have no emergency access to the patient. The clinic's server is up.
// Inputs and system conditions: GET /patients/{id} on the on-site app (ehr_app role), following redirects.
// Test data: Synthetic patient John {family}, born 1999-01-01, where {family} is "Restricted" and eight random hex
//   digits.
// Steps:
//   1. Insert the patient, and don't assign them.
//   2. Sign in as a clinician.
//   3. Open the patient's chart directly.
// Expected result: The page doesn't contain "{family}, John". The answer is a refusal (401, 403 or another
//   unsuccessful status), or a page that asks for a reason or says access is denied.
// Creator: Diego Valenzuela
public class TC_UC4_02_AlternativeFlow(WebApps apps)
    : IClassFixture<WebApps>
{
    [Fact]
    public async Task Unassigned_clinician_cannot_directly_view_chart()
    {
        // Arrange: Create a synthetic patient.
        var family = "Restricted" +
            Guid.NewGuid().ToString("N")[..8];

        var patient = await apps.InsertPatientAsync(
            "John",
            family,
            new DateOnly(1999, 1, 1));

        // Create an authenticated clinician.
        var clinician = await apps.ClinicianAsync();

        // Do NOT assign the patient to this clinician.
        var session = await SignInAsync(
            apps.OnPrem,
            clinician);

        // Act: Attempt to open the patient chart directly.
        using var response = await session.Client.GetAsync(
            $"/patients/{patient}",
            Cancel);

        var html = await response.Content.ReadAsStringAsync(
            Cancel);

        // Assert: Protected chart content must not appear
        // without the required authorization.
        Assert.DoesNotContain(
            $"{family}, John",
            html);

        // Inspect the response to determine whether the
        // application denies access or requires a reason.
        Assert.True(
            response.StatusCode ==
                System.Net.HttpStatusCode.Forbidden ||
            response.StatusCode ==
                System.Net.HttpStatusCode.Unauthorized ||
            !response.IsSuccessStatusCode ||
            html.Contains(
                "reason",
                StringComparison.OrdinalIgnoreCase) ||
            html.Contains(
                "access denied",
                StringComparison.OrdinalIgnoreCase),
            "The clinician should be denied direct chart " +
            "access or required to complete an authorization step.");
    }
}
