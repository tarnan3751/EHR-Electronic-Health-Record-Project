using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-03
// Purpose / scenario: Emergency access is offered for a chart off the panel. Instead of the chart, a clinician who
//   opens a patient not on their panel is offered emergency access, which asks for a reason.
// Type: Functional (alternative flow)
// Requirements: FR-02, FR-06, FR-30
// Use case: UC 4, View a patient's chart
// Preconditions: The clinician is signed in on the on-site app, with password and authenticator code. The patient is
//   not on their panel, and they have no emergency access to the patient. The clinic's server is up.
// Inputs and system conditions: GET /patients/{id} on the on-site app (ehr_app role), following redirects.
// Test data: Synthetic patient John {family}, born 1999-01-01, where {family} is "Restricted" and eight random hex
//   digits.
// Steps:
//   1. Insert the patient, and don't assign them.
//   2. Sign in as a clinician.
//   3. Open the patient's chart.
// Expected result: The page doesn't contain "{family}, John"; it mentions emergency access, and asks for a reason or
//   says the chart is restricted.
// Creator: Diego Valenzuela
public class TC_UC4_03_AlternativeFlow(WebApps apps)
    : IClassFixture<WebApps>
{
    [Fact]
    public async Task Restricted_chart_offers_emergency_access()
    {
        // Arrange: Create a synthetic patient.
        var family = "Restricted" +
            Guid.NewGuid().ToString("N")[..8];

        var patient = await apps.InsertPatientAsync(
            "John",
            family,
            new DateOnly(1999, 1, 1));

        // Create a clinician who is not assigned
        // to the patient.
        var clinician = await apps.ClinicianAsync();

        // Do not call apps.AssignAsync().
        var session = await SignInAsync(
            apps.OnPrem,
            clinician);

        // Act: Attempt to open the patient's chart.
        using var response = await session.Client.GetAsync(
            $"/patients/{patient}",
            Cancel);

        var html = await response.Content.ReadAsStringAsync(
            Cancel);

        // Assert: Normal access is restricted.
        Assert.DoesNotContain(
            $"{family}, John",
            html);

        // Verify that emergency access is offered.
        Assert.Contains(
            "Emergency",
            html,
            StringComparison.OrdinalIgnoreCase);

        // Verify that the page provides an access
        // restriction or reason prompt.
        Assert.True(
            html.Contains(
                "reason",
                StringComparison.OrdinalIgnoreCase) ||
            html.Contains(
                "restricted",
                StringComparison.OrdinalIgnoreCase),
            "The page should explain the access restriction " +
            "or request an emergency access reason.");
    }
}
