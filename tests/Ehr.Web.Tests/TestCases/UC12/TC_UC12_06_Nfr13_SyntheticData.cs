using System.Text.RegularExpressions;
using Ehr.Seed;
using Ehr.Testing;
using Xunit;

namespace Ehr.Web.Tests.TestCases.UC12;

// Test Case ID: TC-UC12-06
// Purpose / scenario: Emergency access is only ever tried on synthetic patients, with reasons that hold no real
//   patient's details: the chart the local stack's setup opens through emergency access, the reason it records, and the
//   charts of the synthetic clinic any clinician can open that way.
// Type: Non-functional (data: synthetic only)
// Requirements: NFR-13, with FR-06
// Use case: UC 12, Open a chart through emergency access
// Preconditions: The repository as committed. No database is needed.
// Inputs and system conditions: The file infra/compose/test-accounts.cs, whose setup opens the example patient's chart
//   through emergency access, and the synthetic clinic (bench/Ehr.Seed).
// Test data: MRN SYN0000001 and the setup's reason, "Setting up a test portal account on the local stack". The first
//   1,000 patients of the synthetic clinic.
// Steps:
//   1. Collect every MRN in test-accounts.cs.
//   2. Read the reason test-accounts.cs records with its emergency access.
//   3. Make the first 1,000 patients of the synthetic clinic.
// Expected result: Step 1 finds MRNs, and every one starts SYN. Step 2's reason says what it is for (a test, on the
//   local stack) and holds no MRN, email address, phone number or date. In step 3 every MRN starts SYN and every given
//   and family name is one from the synthetic clinic's own lists. The check of the grants and reasons stored in the
//   running stack's database is the manual part, TC_UC12_06_Nfr13_SyntheticData.md in this folder.
// Creator: Luke Garcia
public partial class TC_UC12_06_Nfr13_SyntheticData
{
    static string TestAccounts => File.ReadAllText(RepositoryRoot.Combine("infra/compose/test-accounts.cs"));

    [Fact]
    public void The_setup_opens_only_a_synthetic_patients_chart_through_emergency_access()
    {
        var mrns = Mrn().Matches(TestAccounts).Select(m => m.Value).Distinct().ToList();

        Assert.NotEmpty(mrns);
        Assert.All(mrns, mrn => Assert.StartsWith("SYN", mrn));
    }

    [Fact]
    public void The_reason_the_setup_records_is_test_text_with_no_patient_details()
    {
        var reason = Assert.Single(SetupReason().Matches(TestAccounts)).Groups[1].Value;

        Assert.Contains("test", reason, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(reason.Length, 10, 500);
        Assert.DoesNotMatch(Mrn(), reason);
        Assert.DoesNotContain("@", reason);
        Assert.DoesNotMatch("[0-9]", reason);
    }

    [Fact]
    public void Every_chart_open_to_emergency_access_in_the_synthetic_clinic_is_obviously_synthetic()
    {
        foreach (var chart in new SyntheticClinic(1_000).Charts())
        {
            Assert.StartsWith("SYN", chart.Patient.Mrn);
            Assert.Contains(chart.Patient.GivenName, SyntheticClinic.GivenNames(chart.Patient.Gender));
            Assert.Contains(chart.Patient.FamilyName, SyntheticClinic.FamilyNames);
        }
    }

    // An MRN as test-accounts.cs writes one: letters then seven digits, such as SYN0000001.
    [GeneratedRegex(@"\b[A-Z]{3}[0-9]{7}\b")]
    private static partial Regex Mrn();

    // The reason constant in test-accounts.cs's Defaults.
    [GeneratedRegex(@"public const string Reason = ""([^""]*)"";")]
    private static partial Regex SetupReason();
}
