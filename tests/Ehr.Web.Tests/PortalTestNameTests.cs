using Ehr.Web.Areas.Portal.Pages;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Short names and readable units for patients (Tahseen's decision of 26 November 2026, ForPatients): the portal names
// a test by its long name's part before the first " [", and a vital sign as the clinic's form labels it, with UCUM's
// print form for the few units that read badly. Clinicians and the FHIR file read the record's names and units as
// they are.
public class PortalTestNameTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

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
    public void A_unit_that_reads_badly_is_written_as_UCUM_prints_it(string? ucum, string? shown) => Assert.Equal(shown, ForPatients.Unit(ucum));

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

        var (portal, _) = await PortalResultsTests.SignInPatientAsync(apps, apps.OnPrem, patient);
        var results = await portal.GetStringAsync("/portal/results", Cancel);
        var words = Words(results);
        Assert.Contains("<strong>Leukocytes</strong>", results);
        Assert.Contains($"Full name: {LeukocytesName}", words);
        Assert.Contains("value=\"About my Leukocytes result from ", results);
        Assert.Contains("Leukocytes (10³/µL)", words);
        Assert.Contains("Leukocytes: 2 values from", words);
        Assert.Contains("Latest 7.2 10³/µL, up from 6.1.", words);
        Assert.Contains("Oxygen saturation (%)", words);
        Assert.Contains("Oxygen saturation: 2 values from", words);
        Assert.DoesNotContain("Arterial blood", words);

        var print = Words(await portal.GetStringAsync("/portal/record/print", Cancel));
        Assert.Contains("Leukocytes", print);
        Assert.DoesNotContain("[#/volume]", print);
        Assert.Contains("Oxygen saturation", print);
        Assert.DoesNotContain("Arterial blood", print);

        // The clinician's pages and the patient's file keep the record's own names and units.
        var clinical = Words(await session.Client.GetStringAsync(ResultTests.Path(patient, orders[1]), Cancel));
        Assert.Contains(LeukocytesName, clinical);
        Assert.Contains("10*3/uL", clinical);
        Assert.Contains("Oxygen saturation in Arterial blood by Pulse oximetry", Words(await session.Client.GetStringAsync(VitalTests.Path(patient), Cancel)));
        var file = await portal.GetStringAsync("/portal/record?handler=File", Cancel);
        Assert.Contains(LeukocytesName, file);
        Assert.Contains("10*3/uL", file);
    }
}
