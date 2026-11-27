using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.PatientSearchTests;

namespace Ehr.Web.Tests.TestCases.UC3;

// Test Case ID: TC-UC3-02
// Purpose / scenario: Reject an invalid date of birth. A search whose date of birth isn't a real date asks for a
//   correction and lists nobody.
// Type: Functional (invalid input)
// Requirements: FR-01
// Use case: UC 3, Search for a patient
// Preconditions: The clinician is signed in on the on-site app, with password and authenticator code. The clinic's
//   server is up.
// Inputs and system conditions: GET /patients?name=John%20Doe&birthdate=13%2F45%2F1999&mrn=123456789 on the on-site
//   app (ehr_app role).
// Test data: Name "John Doe", date of birth "13/45/1999" (month 13, day 45), MRN "123456789". All synthetic.
// Steps:
//   1. Sign in as a clinician.
//   2. Search with the invalid date of birth.
// Expected result: The page says "Date of birth must be a full date", shows no "Search results", and lists no
//   chart link.
// Creator: Diego Valenzuela
public class TC_UC3_02_InvalidInput(WebApps apps)
    : IClassFixture<WebApps>
{
    [Fact]
    public async Task Invalid_date_of_birth_is_rejected()
    {
        // Arrange: Sign in as an authorized clinician.
        var session = await SignInAsync(
            apps.OnPrem,
            await apps.ClinicianAsync());

        // Use the invalid DOB specified in TC-UC3-02.
        var invalidDob = "13/45/1999";

        // Act: Attempt a patient search.
        var html = await session.Client.GetStringAsync(
            $"/patients?name={Uri.EscapeDataString("John Doe")}" +
            $"&birthdate={Uri.EscapeDataString(invalidDob)}" +
            $"&mrn=123456789",
            Cancel);

        // Assert: The system displays a validation message.
        Assert.Contains(
            "Date of birth must be a full date",
            html);

        // No patient search results should be displayed.
        Assert.DoesNotContain("Search results", html);

        // No patient chart links should be returned.
        Assert.Empty(Listed(html));
    }
}
