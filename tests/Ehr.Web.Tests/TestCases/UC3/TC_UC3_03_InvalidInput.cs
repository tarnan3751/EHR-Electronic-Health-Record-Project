using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.PatientSearchTests;

namespace Ehr.Web.Tests.TestCases.UC3;

// Test Case ID: TC-UC3-03
// Purpose / scenario: Ask for a criterion when the search is empty. A search submitted with every field empty asks
//   for a name, date of birth or MRN, and lists nobody.
// Type: Functional (invalid input)
// Requirements: FR-01
// Use case: UC 3, Search for a patient
// Preconditions: The clinician is signed in on the on-site app, with password and authenticator code. The clinic's
//   server is up.
// Inputs and system conditions: GET /patients?name=&birthdate=&mrn= on the on-site app (ehr_app role).
// Test data: None: every field is empty.
// Steps:
//   1. Sign in as a clinician.
//   2. Submit the search with every field empty.
// Expected result: The page says "Enter a name, date of birth, or MRN", shows no "Search results", and lists no chart
//   link.
// Creator: Diego Valenzuela
public class TC_UC3_03_InvalidInput(WebApps apps)
    : IClassFixture<WebApps>
{
    [Fact]
    public async Task Empty_search_criteria_are_rejected()
    {
        // Arrange: Sign in as an authorized clinician.
        var session = await SignInAsync(
            apps.OnPrem,
            await apps.ClinicianAsync());

        // Act: Submit a search with all fields empty.
        var html = await session.Client.GetStringAsync(
            "/patients?name=&birthdate=&mrn=",
            Cancel);

        // Assert: The system should prompt the clinician
        // to provide identifying information.
        Assert.Contains(
            "Enter a name, date of birth, or MRN",
            html,
            StringComparison.OrdinalIgnoreCase);

        // No search results should be displayed.
        Assert.DoesNotContain("Search results", html);

        // No patient chart links should be returned.
        Assert.Empty(Listed(html));
    }
}
