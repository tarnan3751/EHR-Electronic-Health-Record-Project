using System.Text.RegularExpressions;
using Ehr.Seed;
using Ehr.Testing;
using Xunit;

namespace Ehr.Web.Tests.TestCases.UC11;

// Test Case ID: TC-UC11-06
// Purpose / scenario: Every account and patient that sign-in testing uses is synthetic: the example accounts the
//   local stack is set up with, the patient the example portal account belongs to, the synthetic clinic, and the
//   accounts the tests create.
// Type: Non-functional (data: synthetic only)
// Requirements: NFR-13, with FR-27
// Use case: UC 11, Sign in with multi-factor authentication
// Preconditions: The repository as committed. No database is needed.
// Inputs and system conditions: The files infra/compose/test-accounts.cs and infra/compose/.env.example, the synthetic
//   clinic (bench/Ehr.Seed), and the test fixture's address generator (WebApps.NewEmail).
// Test data: The first 1,000 patients of the synthetic clinic. The example accounts admin@example.com,
//   clinician@example.com and patient@example.com, and MRN SYN0000001.
// Steps:
//   1. Collect every email address in test-accounts.cs and .env.example.
//   2. Collect every MRN in test-accounts.cs.
//   3. Make the first 1,000 patients of the synthetic clinic.
//   4. Make an address with the test fixture.
// Expected result: Step 1 finds addresses, and every one is at example.com, a domain reserved for examples. Step 2
//   finds MRNs, and every one starts SYN. In step 3 every MRN starts SYN and every given and family name is one from
//   the synthetic clinic's own lists. Step 4's address is at example.test, a reserved test domain. The check against
//   the running stack's database is the manual part, TC_UC11_06_Nfr13_SyntheticData.md in this folder.
// Creator: Luke Garcia
public partial class TC_UC11_06_Nfr13_SyntheticData
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
    public void The_example_portal_account_belongs_to_a_synthetic_patient()
    {
        var text = File.ReadAllText(RepositoryRoot.Combine("infra/compose/test-accounts.cs"));

        var mrns = Mrn().Matches(text).Select(m => m.Value).Distinct().ToList();

        Assert.NotEmpty(mrns);
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
    public void The_tests_create_accounts_only_at_a_reserved_test_domain() =>
        Assert.EndsWith("@example.test", WebApps.NewEmail());

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailAddress();

    // An MRN as test-accounts.cs writes one: letters then seven digits, such as SYN0000001.
    [GeneratedRegex(@"\b[A-Z]{3}[0-9]{7}\b")]
    private static partial Regex Mrn();
}
