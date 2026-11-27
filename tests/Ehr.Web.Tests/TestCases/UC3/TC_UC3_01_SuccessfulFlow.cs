using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.PatientSearchTests;

namespace Ehr.Web.Tests.TestCases.UC3;

// Test Case ID: TC-UC3-01
// Purpose / scenario: Find a patient by name, date of birth and MRN. A clinician searches with all three, and the
//   results list exactly the patient they describe and not another patient with the same family name.
// Type: Functional (successful flow)
// Requirements: FR-01
// Use case: UC 3, Search for a patient
// Preconditions: The clinician is signed in on the on-site app, with password and authenticator code. The clinic's
//   server is up.
// Inputs and system conditions: GET /patients?name={family}&birthdate=1999-01-01&mrn={mrn} on the on-site app
//   (ehr_app role).
// Test data: Synthetic patient John {family}, born 1999-01-01, with the MRN the test database gives them; and an
//   unrelated synthetic patient Jane {family}, born 1995-05-15. {family} is "Doe" and eight random hex digits, so no
//   other test's patient matches.
// Steps:
//   1. Insert both patients.
//   2. Sign in as a clinician.
//   3. Search with John's family name, date of birth and MRN.
// Expected result: The page shows "Search results" and lists exactly one chart link, John's; Jane's chart isn't linked.
// Creator: Diego Valenzuela
public class TC_UC3_01_SuccessfulFlow(WebApps apps)
    : IClassFixture<WebApps>
{
    [Fact]
    public async Task Search_with_valid_patient_information()
    {
        // Arrange: Create a unique synthetic patient.
        var family = "Doe" + Guid.NewGuid().ToString("N")[..8];

        var patient = await apps.InsertPatientAsync(
            "John",
            family,
            new DateOnly(1999, 1, 1));

        var mrn = WebApps.MrnOf(patient);

        // Create an unrelated patient.
        var unrelated = await apps.InsertPatientAsync(
            "Jane",
            family,
            new DateOnly(1995, 5, 15));

        // Sign in as an authorized clinician.
        var session = await SignInAsync(
            apps.OnPrem,
            await apps.ClinicianAsync());

        // Act: Search using name, DOB, and MRN.
        var html = await session.Client.GetStringAsync(
            $"/patients?name={Uri.EscapeDataString(family)}" +
            $"&birthdate=1999-01-01&mrn={mrn}",
            Cancel);

        // Assert: Search results contain exactly John.
        Assert.Contains("Search results", html);
        Assert.Equal([patient], Listed(html));

        // Verify the unrelated patient is excluded.
        Assert.DoesNotContain($"/patients/{unrelated}", html);
    }
}
