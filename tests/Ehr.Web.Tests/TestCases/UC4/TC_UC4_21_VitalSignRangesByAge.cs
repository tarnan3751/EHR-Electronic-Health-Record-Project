using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VitalTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-21
// Purpose / scenario: A child's heart rate, respiratory rate and blood pressure are flagged against the range for the
//   child's age, not an adult's; temperature has one range at every age.
// Type: Functional (alternative flow: reference ranges by age)
// Requirements: FR-08, proposed FR-50
// Use case: UC 4, View patient chart
// Preconditions: The clinician is signed in on the on-site app with two patients on their panel: an adult, and a
//   child who is three years old.
// Inputs and system conditions: POST /patients/{id}/vitals for each.
// Test data: For both: heart rate 120, respiratory rate 28, blood pressure 106/66, temperature 38.2.
// Steps:
//   1. Record the set for the adult.
//   2. Record the same set for the child.
// Expected result: Step 1: heart rate, respiratory rate and temperature are tagged High; 106/66 is not. Step 2: the
//   heart rate and respiratory rate are not tagged (a three-year-old's ranges are Fleming's 70 to 136 and 17 to 33,
//   step 9d); the blood pressure, one reading (step 9c), is tagged "Needs follow-up", not High (both numbers at or above
//   Table 6's 101 and 58 for a three-year-old whose sex is unknown); the temperature is tagged High, as the adult's is.
//   Each stored value has the range for the patient's age.
// Creator: Tahseen Arnan
public class TC_UC4_21_VitalSignRangesByAge(WebApps apps) : IClassFixture<WebApps>
{
    static readonly (string Code, string Value)[] Set = [(HeartRate, "120"), ("9279-1", "28"), (Systolic, "106"), (Diastolic, "66"), (Temperature, "38.2")];

    static Task<List<string>> RangesAsync(WebApps apps, Guid patient) => ProxyTests.QueryAsync(apps,
        """
        SELECT code || ' ' || coalesce(trim_scale(reference_low)::text, 'none') || '-' || coalesce(trim_scale(reference_high)::text, 'none')
            || CASE WHEN reference_kind = 'screening' THEN CASE WHEN value >= reference_high THEN ' needs follow-up' ELSE '' END
                    WHEN value > reference_high THEN ' high' WHEN value < reference_low THEN ' low' ELSE '' END
        FROM ehr.observations WHERE patient_id = @patient AND category = 'vital-signs' ORDER BY code
        """, patient);

    [Fact]
    public async Task A_childs_vital_signs_are_flagged_against_the_range_for_their_age()
    {
        var (clinician, adult, session) = await OnPanelAsync(apps);
        var child = await apps.InsertPatientAsync("Synthetic", "Child");
        await apps.AssignAsync(clinician, child);
        await ProxyTests.QueryAsync(apps, "UPDATE ehr.patients SET birth_date = (now() - interval '3 years 2 months')::date WHERE id = @patient RETURNING id::text", child);
        await ProxyTests.QueryAsync(apps, "UPDATE ehr.patients SET birth_date = DATE '1980-05-01' WHERE id = @patient RETURNING id::text", adult);

        using var forAdult = await RecordAsync(apps, session.Client, adult, 10, Set);
        var grown = await ReadAsync(forAdult);
        using var forChild = await RecordAsync(apps, session.Client, child, 10, Set);
        var young = await ReadAsync(forChild);

        Assert.Equal(["8310-5 36.1-37.5 high", $"{Diastolic} 60-79", $"{Systolic} 90-129", $"{HeartRate} 60-100 high", "9279-1 12-20 high"], await RangesAsync(apps, adult));
        Assert.Equal(["8310-5 36.1-37.5 high", $"{Diastolic} none-58 needs follow-up", $"{Systolic} none-101 needs follow-up", $"{HeartRate} 70-136", "9279-1 17-33"], await RangesAsync(apps, child));
        static string Row(string page, string name) => System.Text.RegularExpressions.Regex.Match(page, $"(?s)<th scope=\"row\">{name}.*?</tr>").Value;
        Assert.Contains("High", Row(grown, "Heart rate"));
        Assert.DoesNotContain("High", Row(grown, "Blood pressure"));
        Assert.DoesNotContain("High", Row(young, "Heart rate"));
        Assert.DoesNotContain("High", Row(young, "Respiratory rate"));
        Assert.Contains("Needs follow-up", Row(young, "Blood pressure"));
        Assert.DoesNotContain("High", Row(young, "Blood pressure"));
        Assert.Contains("High", Row(young, "Temperature"));
    }

    // A trend marks each point against the range it was recorded with: a child who has since grown into the next age
    // group keeps the earlier reading unmarked if it was inside the range for their age then.
    [Fact]
    public async Task A_trend_marks_each_reading_against_the_range_for_the_age_it_was_taken_at()
    {
        var (clinician, _, session) = await OnPanelAsync(apps);
        var child = await apps.InsertPatientAsync("Synthetic", "Growing");
        await apps.AssignAsync(clinician, child);
        // Six years and a month old: in Fleming's 4 to 6 years two months ago (heart rate up to 131), 6 to 8 now (123).
        await ProxyTests.QueryAsync(apps, "UPDATE ehr.patients SET birth_date = (now() - interval '6 years 1 month')::date WHERE id = @patient RETURNING id::text", child);

        using var before = await RecordAsync(apps, session.Client, child, 62 * 24 * 60, (HeartRate, "128"));
        using var now = await RecordAsync(apps, session.Client, child, 10, (HeartRate, "100"));
        var page = await ReadAsync(now);

        Assert.Equal([$"{HeartRate} 65-131", $"{HeartRate} 59-123"], await ProxyTests.QueryAsync(apps,
            "SELECT code || ' ' || trim_scale(reference_low) || '-' || trim_scale(reference_high) FROM ehr.observations WHERE patient_id = @patient ORDER BY effective_at", child));
        Assert.DoesNotContain("sc-trend__point--outside", page);
        Assert.DoesNotContain("128, outside the range", page);
        Assert.Contains("Reference range 59 to 123. That range is for the latest reading. Earlier ones are marked against the range for the patient's age then.", Words(page));
        Assert.DoesNotContain("High", System.Text.RegularExpressions.Regex.Match(page, "(?s)<th scope=\"row\">Heart rate.*?</tr>").Value);
    }
}
