using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.PatientSearchTests;

namespace Ehr.Web.Tests.TestCases.UC3;

// Test Case ID: TC-UC3-04
// Purpose / scenario: No patient matches the criteria. A valid search that describes nobody in the clinic says so and
//   lists nobody.
// Type: Functional (alternative flow)
// Requirements: FR-01
// Use case: UC 3, Search for a patient
// Preconditions: The clinician is signed in on the on-site app, with password and authenticator code. The clinic's
//   server is up. No patient has the family name used.
// Inputs and system conditions: GET /patients?name={name}&birthdate=1995-05-15&mrn=999999999 on the on-site app
//   (ehr_app role).
// Test data: Name "Smith" and eight random hex digits, date of birth 1995-05-15, MRN "999999999". All synthetic.
// Steps:
//   1. Sign in as a clinician.
//   2. Search with the name, date of birth and MRN.
// Expected result: The page says "No patients match." and lists no chart link.
// Creator: Diego Valenzuela
public class TC_UC3_04_AlternativeFlow(WebApps apps)
    : IClassFixture<WebApps>
{
    [Fact]
    public async Task No_matching_patients_displays_message()
    {
        // Arrange: Sign in as an authorized clinician.
        var session = await SignInAsync(
            apps.OnPrem,
            await apps.ClinicianAsync());

        // Use a unique name to ensure no patient matches.
        var nonexistentName =
            "Smith" + Guid.NewGuid().ToString("N")[..8];

        // Act: Search using valid identifying information
        // that does not match any existing patient.
        var html = await session.Client.GetStringAsync(
            $"/patients?name={Uri.EscapeDataString(nonexistentName)}" +
            $"&birthdate=1995-05-15" +
            $"&mrn=999999999",
            Cancel);

        // Assert: The system displays a no-match message.
        Assert.Contains("No patients match.", html);

        // No patient records should be listed.
        Assert.Empty(Listed(html));
    }
}
