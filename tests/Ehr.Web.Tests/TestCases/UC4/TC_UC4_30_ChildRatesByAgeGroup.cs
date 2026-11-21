using System.Text.RegularExpressions;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VitalTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-30
// Purpose / scenario: A child's heart and respiratory rates are flagged against Fleming et al.'s 1st and 99th centiles
//   for their age group (step 9d), which change on the birthday and not before; a newborn's against the group from
//   birth to 3 months.
// Type: Functional (alternative flow: reference ranges by age)
// Requirements: FR-08, proposed FR-50
// Use case: UC 4, View patient chart
// Preconditions: The clinician is signed in on the on-site app with two children on their panel: one whose second
//   birthday was on the 15th of the month before last, and one born ten days ago.
// Inputs and system conditions: POST /patients/{id}/vitals; GET /patients/{id}/vitals.
// Test data: A respiratory rate of 40 at noon (UTC) on the day before the second birthday and on the birthday; a
//   newborn's heart rate of 105.
// Steps:
//   1. Record the two respiratory rates for the first child.
//   2. Record the newborn's heart rate.
// Expected result: Step 1: the day before the birthday, 40 is inside 18 to 24 months' 19 to 46 and untagged; on the
//   birthday it is above 2 to 3 years' 18 to 38 and tagged High. Step 2: 105 is below 0 to 3 months' 107 and tagged Low.
// Creator: Tahseen Arnan
public class TC_UC4_30_ChildRatesByAgeGroup(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    static string Row(string page, string name) => Regex.Match(page, $"(?s)<th scope=\"row\">{name}.*?</tr>").Value;

    [Fact]
    public async Task A_childs_rates_are_flagged_against_their_age_group_from_the_birthday_on()
    {
        var (clinician, child, session) = await OnPanelAsync(apps);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var birthday = new DateOnly(today.Year, today.Month, 15).AddMonths(-2);
        await ProxyTests.QueryAsync(apps, $"UPDATE ehr.patients SET birth_date = '{birthday.AddYears(-2):yyyy-MM-dd}' WHERE id = @patient RETURNING id::text", child);

        foreach (var day in new[] { birthday.AddDays(-1), birthday })
        {
            using var recorded = await RecordAtAsync(session.Client, child, $"{day:yyyy-MM-dd}T12:00", ("9279-1", "40"));
            Assert.Contains("Saved:", await ReadAsync(recorded));
        }

        Assert.Equal(["9279-1 19-46", "9279-1 18-38"], await ProxyTests.QueryAsync(apps,
            "SELECT code || ' ' || trim_scale(reference_low) || '-' || trim_scale(reference_high) FROM ehr.observations WHERE patient_id = @patient ORDER BY effective_at", child));
        // Newest first: the birthday's 40 tagged High, the day before's not.
        Assert.Matches("(?s)<td class=\"sc-table__mono\">\\s*40\\s*<span class=\"sc-tag sc-tag--warning\">High</span>\\s*</td>\\s*<td class=\"sc-table__mono\">\\s*40\\s*</td>",
            Row(await session.Client.GetStringAsync(Path(child), Cancel), "Respiratory rate"));

        var newborn = await apps.InsertPatientAsync("Synthetic", "Newborn", today.AddDays(-10));
        await apps.AssignAsync(clinician, newborn);
        using var rate = await RecordAsync(session.Client, newborn, 10, (HeartRate, "105"));
        Assert.Matches("(?s)<td class=\"sc-table__mono\">\\s*105\\s*<span class=\"sc-tag sc-tag--warning\">Low</span>", Row(await ReadAsync(rate), "Heart rate"));
        Assert.Equal(["8867-4 107-181"], await ProxyTests.QueryAsync(apps,
            "SELECT code || ' ' || trim_scale(reference_low) || '-' || trim_scale(reference_high) FROM ehr.observations WHERE patient_id = @patient", newborn));
    }
}
