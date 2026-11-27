using System.Text.RegularExpressions;
using Ehr.Seed;
using Ehr.Testing;
using Xunit;

namespace Ehr.Web.Tests.TestCases.UC5;

// Test Case ID: TC-UC5-06
// Purpose / scenario: Registration testing uses only synthetic data. Registration and portal testing use only
//   synthetic patients and addresses at reserved domains: the example accounts the local stack is set up with, the
//   patient the example portal account belongs to, the synthetic clinic, and the addresses the registration tests
//   register.
// Type: Non-functional (NFR-13)
// Requirements: NFR-13, with FR-15
// Use case: UC 5, Register for the patient portal
// Preconditions: The repository as committed. No database is needed.
// Inputs and system conditions: The example accounts (infra/compose/test-accounts.cs), the stack's settings example
//   (infra/compose/.env.example), the synthetic clinic (bench/Ehr.Seed), and the test fixture's address generator that
//   the registration tests use (WebApps.NewEmail). These are the checks of TC-UC11-06, here for registration.
// Test data: patient@example.com and MRN SYN0000001; the first 1,000 patients of the synthetic clinic; an address made
//   by the test fixture.
// Steps:
//   1. Collect every email address in test-accounts.cs and .env.example.
//   2. Collect every MRN in test-accounts.cs.
//   3. Make the first 1,000 patients of the synthetic clinic.
//   4. Make an address with the test fixture, as the registration tests do.
// Expected result: Every address is at example.com, every MRN starts SYN, every synthetic patient has a SYN MRN and a
//   name from the synthetic clinic's own lists, and the fixture's address is at example.test.
// Creator: Tahseen Arnan
public partial class TC_UC5_06_Nfr13_SyntheticData
{
    [Theory]
    [InlineData("infra/compose/test-accounts.cs")]
    [InlineData("infra/compose/.env.example")]
    public void The_example_accounts_use_only_reserved_example_addresses(string file)
    {
        var text = File.ReadAllText(RepositoryRoot.Combine(file));

        var addresses = EmailAddress().Matches(text).Select(m => m.Value).Distinct().ToList();

        Assert.NotEmpty(addresses);
        Assert.All(addresses, address => Assert.EndsWith("@example.com", address));
    }

    [Fact]
    public void The_example_portal_account_is_registered_for_a_synthetic_patient()
    {
        var text = File.ReadAllText(RepositoryRoot.Combine("infra/compose/test-accounts.cs"));

        var mrns = Mrn().Matches(text).Select(m => m.Value).Distinct().ToList();

        Assert.Contains("patient@example.com", text);
        Assert.Contains("SYN0000001", mrns);
        Assert.All(mrns, mrn => Assert.StartsWith("SYN", mrn));
    }

    [Fact]
    public void The_synthetic_clinics_patients_are_obviously_synthetic()
    {
        foreach (var chart in new SyntheticClinic(1_000).Charts())
        {
            Assert.StartsWith("SYN", chart.Patient.Mrn);
            Assert.Contains(chart.Patient.GivenName, SyntheticClinic.GivenNames(chart.Patient.Gender));
            Assert.Contains(chart.Patient.FamilyName, SyntheticClinic.FamilyNames);
        }
    }

    [Fact]
    public void The_registration_tests_register_addresses_at_a_reserved_test_domain() =>
        Assert.EndsWith("@example.test", WebApps.NewEmail());

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailAddress();

    // An MRN as test-accounts.cs writes one: letters then seven digits, such as SYN0000001.
    [GeneratedRegex(@"\b[A-Z]{3}[0-9]{7}\b")]
    private static partial Regex Mrn();
}
