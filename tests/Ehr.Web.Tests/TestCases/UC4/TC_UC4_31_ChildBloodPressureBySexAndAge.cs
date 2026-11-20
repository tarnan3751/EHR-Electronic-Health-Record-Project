using System.Text.RegularExpressions;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VitalTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-31
// Purpose / scenario: A child's blood pressure goes by sex as well as age (step 9d): the same reading needs follow-up
//   for a boy and not for a girl, and a child whose sex is other or unknown is held to the lower of the two values. On
//   the 18th birthday a reading moves from the screening value to the adults' range, and its label with it.
// Type: Functional (alternative flow: a child's blood pressure)
// Requirements: FR-08, proposed FR-50
// Use case: UC 4, View patient chart
// Preconditions: The clinician is signed in on the on-site app with five patients on their panel: a boy, a girl, a child
//   whose sex is other and one whose sex is unknown, each 1 year 6 months old (Table 6 at 1 year: a boy's diastolic
//   value 52, a girl's 54); and a young woman whose 18th birthday was on the 15th of the month before last.
// Inputs and system conditions: POST /patients/{id}/vitals; GET /patients/{id}/vitals.
// Test data: 97/53 for each child; for the young woman, 125/75 at noon (UTC) on the day before her birthday and on
//   it, and 131/75 the day after.
// Steps:
//   1. Record each child's blood pressure.
//   2. Record the young woman's three.
// Expected result: Step 1: the boy's, the other child's and the unknown child's 97/53 are tagged "Needs follow-up"
//   (53 is at or above 52); the girl's is untagged (53 is under 54). Step 2: the day before the birthday 125/75 is tagged
//   "Needs follow-up" (at or above 120); on the birthday the same reading is untagged (inside the adults' 90 to 129 and
//   60 to 79), and the day after 131/75 is tagged High.
// Creator: Tahseen Arnan
public class TC_UC4_31_ChildBloodPressureBySexAndAge(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    static string Row(string page) => Regex.Match(page, "(?s)<th scope=\"row\">Blood pressure.*?</tr>").Value;

    [Fact]
    public async Task A_childs_blood_pressure_goes_by_sex_and_the_label_changes_on_the_eighteenth_birthday()
    {
        var (clinician, _, session) = await OnPanelAsync(apps);
        foreach (var (sex, expected) in new[] { ("male", "Needs follow-up"), ("female", null), ("other", "Needs follow-up"), ("unknown", "Needs follow-up") })
        {
            var child = await apps.InsertPatientAsync("Synthetic", $"Child {sex}", DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-18));
            await apps.AssignAsync(clinician, child);
            await ProxyTests.QueryAsync(apps, $"UPDATE ehr.patients SET gender = '{sex}' WHERE id = @patient RETURNING id::text", child);
            using var recorded = await RecordAsync(session.Client, child, 10, (Systolic, "97"), (Diastolic, "53"));
            var cell = Regex.Match(Row(await ReadAsync(recorded)), "(?s)<td class=\"sc-table__mono\">.*?</td>").Value;
            Assert.Contains("97/53", cell);
            if (expected is null)
            {
                Assert.DoesNotContain("sc-tag", cell);
            }
            else
            {
                Assert.Contains($"<span class=\"sc-tag sc-tag--warning\">{expected}</span>", cell);
            }
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var birthday = new DateOnly(today.Year, today.Month, 15).AddMonths(-2);
        var adult = await apps.InsertPatientAsync("Synthetic", "Eighteen", birthday.AddYears(-18));
        await apps.AssignAsync(clinician, adult);
        await ProxyTests.QueryAsync(apps, "UPDATE ehr.patients SET gender = 'female' WHERE id = @patient RETURNING id::text", adult);
        foreach (var (day, systolic) in new[] { (birthday.AddDays(-1), "125"), (birthday, "125"), (birthday.AddDays(1), "131") })
        {
            using var recorded = await RecordAtAsync(session.Client, adult, $"{day:yyyy-MM-dd}T12:00", (Systolic, systolic), (Diastolic, "75"));
            Assert.Contains("Saved:", await ReadAsync(recorded));
        }

        // Newest first.
        Assert.Matches(
            "(?s)<td class=\"sc-table__mono\">\\s*131/75\\s*<span class=\"sc-tag sc-tag--warning\">High</span>\\s*</td>\\s*"
            + "<td class=\"sc-table__mono\">\\s*125/75\\s*</td>\\s*<td class=\"sc-table__mono\">\\s*125/75\\s*<span class=\"sc-tag sc-tag--warning\">Needs follow-up</span>\\s*</td>",
            Row(await session.Client.GetStringAsync(Path(adult), Cancel)));
    }
}
