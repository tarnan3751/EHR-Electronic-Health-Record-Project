using System.Net;
using Ehr.Data.Patients;
using Ehr.Domain.Results;
using Ehr.Web.Areas.Portal.Pages;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Short names and readable units for patients (Tahseen's decision of 26 November 2026, ForPatients): the portal names
// a test by its long name's part before the first " [", and a vital sign as the clinic's form labels it, with UCUM's
// print form for the few units that read badly (Units.ForPatients), in a result's value, range and meaning too.
// Clinicians and the FHIR file read the record's names and units as they are.
public class PortalTestNameTests(WebApps apps) : IClassFixture<WebApps>
{
    const string Leukocytes = "6690-2";

    const string LeukocytesName = "Leukocytes [#/volume] in Blood by Automated count";

    [Theory]
    [InlineData("4544-3", "Hematocrit [Volume Fraction] of Blood by Automated count", "Hematocrit")]
    [InlineData(Leukocytes, LeukocytesName, "Leukocytes")]
    [InlineData("718-7", "Hemoglobin [Mass/volume] in Blood", "Hemoglobin")]
    [InlineData("13457-7", "Cholesterol in LDL [Mass/volume] in Serum or Plasma by calculation", "Cholesterol in LDL")]
    [InlineData("4548-4", "Hemoglobin A1c/Hemoglobin.total in Blood", "Hemoglobin A1c/Hemoglobin.total in Blood")]
    [InlineData(null, "Glucose", "Glucose")]
    [InlineData("59408-5", "Oxygen saturation in Arterial blood by Pulse oximetry", "Oxygen saturation")]
    [InlineData("8310-5", "Body temperature", "Temperature")]
    [InlineData("39156-5", "Body mass index (BMI) [Ratio]", "Body mass index")]
    public void A_test_is_named_by_its_name_before_the_first_bracket_and_a_vital_sign_as_the_form_labels_it(string? code, string display, string name) =>
        Assert.Equal(name, ForPatients.Name(code, display));

    [Theory]
    [InlineData("10*3/uL", "10³/µL")]
    [InlineData("10*6/uL", "10⁶/µL")]
    [InlineData("m[IU]/L", "mIU/L")]
    [InlineData("mm[Hg]", "mmHg")]
    [InlineData("/min", "per minute")]
    [InlineData("mg/dL", "mg/dL")]
    [InlineData(null, null)]
    public void A_unit_that_reads_badly_is_written_as_UCUM_prints_it(string? ucum, string? shown)
    {
        Assert.Equal(shown, ForPatients.Unit(ucum));
        Assert.Equal(shown, Units.ForPatients(ucum));
    }

    [Fact]
    public void A_results_value_range_and_meaning_write_the_unit_as_its_name_and_trend_do()
    {
        var result = new Observation
        {
            Id = Guid.CreateVersion7(),
            PatientId = Guid.CreateVersion7(),
            Code = Leukocytes,
            Display = LeukocytesName,
            Status = ObservationStatus.Final,
            Value = 12.4m,
            Unit = "10*3/uL",
            ReferenceLow = 4.5m,
            ReferenceHigh = 11.0m,
            EffectiveAt = DateTimeOffset.UtcNow,
            IssuedAt = DateTimeOffset.UtcNow,
        };

        Assert.Equal("12.4 10³/µL", ResultExplanation.Value(result));
        Assert.Equal("4.5 to 11 10³/µL", ResultExplanation.Range(result));
        Assert.Equal("Your result is above the normal range (4.5 to 11 10³/µL).", ResultExplanation.Meaning(result));
    }

    [Fact]
    public async Task The_portal_names_a_filed_result_and_a_vital_sign_shortly_and_the_clinic_and_the_file_as_before()
    {
        var (_, patient, session) = await OrderTests.OnPanelAsync(apps);
        var orders = new List<Guid>();
        foreach (var value in new[] { "6.1", "7.2" })
        {
            var order = await OrderTests.SignedAsync(session.Client, patient, "lab", "Complete blood count (CBC)", OrderTests.Cbc);
            using var filed = await ResultTests.FileAsync(apps, session.Client, patient, order, (Leukocytes, value));
            orders.Add(order);
        }

        using var earlier = await VitalTests.RecordAsync(apps, session.Client, patient, 30, (Ehr.Data.Patients.VitalSign.OxygenSaturation, "95"));
        using var later = await VitalTests.RecordAsync(apps, session.Client, patient, 10, (Ehr.Data.Patients.VitalSign.OxygenSaturation, "97"));
        // A thyrotropin in m[IU]/L, high: the other unit written for patients, in the meaning's sentence too.
        var day = DateTimeOffset.UtcNow.AddDays(-1);
        await apps.InsertResultAsync(patient, "Thyrotropin [Units/volume] in Serum or Plasma", "3016-3", 5.2m, "m[IU]/L", 0.4m, 4.0m, day, day);

        var (portal, _) = await PortalResultsTests.SignInPatientAsync(apps, apps.OnPrem, patient);
        var results = await portal.GetStringAsync("/portal/results", Cancel);
        var words = Words(results);
        Assert.Contains("<strong>Leukocytes</strong>", results);
        Assert.Contains($"Full name: {LeukocytesName}", words);
        Assert.Contains("value=\"About my Leukocytes result from ", results);
        Assert.Contains("Leukocytes (10³/µL)", words);
        // The result's value, range and meaning say the unit as the trend does, and nothing on the page says it in code.
        var decoded = WebUtility.HtmlDecode(results);
        Assert.Contains("<dd>7.2 10³/µL</dd>", decoded);
        Assert.Contains("<dd>4.5 to 11 10³/µL</dd>", decoded);
        Assert.Contains("Your result is in the normal range (4.5 to 11 10³/µL).", words);
        Assert.DoesNotContain("10*3/uL", decoded);
        Assert.Contains("<dd>5.2 mIU/L</dd>", decoded);
        Assert.Contains("<dd>0.4 to 4 mIU/L</dd>", decoded);
        Assert.Contains("Your result is above the normal range (0.4 to 4 mIU/L).", words);
        Assert.DoesNotContain("m[IU]/L", decoded);
        Assert.Contains("Leukocytes, 2 values from", words);
        Assert.Contains("Latest 7.2 10³/µL, up from 6.1.", words);
        Assert.Contains("Oxygen saturation (%)", words);
        Assert.Contains("Oxygen saturation, 2 values from", words);
        Assert.DoesNotContain("Arterial blood", words);

        var printHtml = await portal.GetStringAsync("/portal/record/print", Cancel);
        var printed = WebUtility.HtmlDecode(printHtml);
        Assert.Contains("<td>7.2 10³/µL</td>", printed);
        Assert.Contains("<td>4.5 to 11 10³/µL</td>", printed);
        Assert.DoesNotContain("10*3/uL", printed);
        Assert.Matches("<td>5\\.2 mIU/L\\s*<span class=\"sc-tag sc-tag--warning\">High</span>\\s*</td>", printed);
        Assert.Contains("<td>0.4 to 4 mIU/L</td>", printed);
        Assert.DoesNotContain("m[IU]/L", printed);
        var print = Words(printHtml);
        Assert.Contains("Leukocytes", print);
        Assert.DoesNotContain("[#/volume]", print);
        Assert.Contains("Oxygen saturation", print);
        Assert.DoesNotContain("Arterial blood", print);

        // The clinician's results page and the patient's file keep the record's own names and units. The clinic's vital
        // signs page names a sign as its form does, in its trends as in its rows.
        var clinical = Words(await session.Client.GetStringAsync(ResultTests.Path(patient, orders[1]), Cancel));
        Assert.Contains(LeukocytesName, clinical);
        Assert.Contains("10*3/uL", clinical);
        var vitals = Words(await session.Client.GetStringAsync(VitalTests.Path(patient), Cancel));
        Assert.Contains("Oxygen saturation (%)", vitals);
        Assert.Contains("Oxygen saturation, 2 values from", vitals);
        Assert.DoesNotContain("Arterial blood", vitals);
        var file = await portal.GetStringAsync("/portal/record?handler=File", Cancel);
        Assert.Contains(LeukocytesName, file);
        Assert.Contains("10*3/uL", file);
    }
}
