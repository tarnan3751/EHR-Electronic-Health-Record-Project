using Ehr.Domain.Results;

namespace Ehr.Web.Areas.Portal.Pages;

// How the portal names a test or a vital sign to a patient, and writes its unit (Tahseen's decision of 26 November
// 2026), in this one place. The forms are Ehr.Domain's, which clinician pages use too (TestNames, Units), and which a
// result's value and range use (ResultExplanation): "Hematocrit" for "Hematocrit [Volume Fraction] of Blood by
// Automated count", "Oxygen saturation", "10³/µL". The record keeps LOINC's names and UCUM's units, and the FHIR file
// and the FHIR API read them unchanged. The results page still gives the long name, under the result.
public static class ForPatients
{
    public static string Name(string? code, string display) => TestNames.Short(code, display);

    // UCUM's print form for the units whose code reads badly, and any other as it is: as clinicians see it. The forms
    // are in Ehr.Domain (Units), so a result's value, range and meaning (ResultExplanation) say the same.
    public static string? Unit(string? ucum) => Units.ForPatients(ucum);

    // Trends (step 9) with each one's name and unit as above: the chart's title, its sentence and its table.
    public static IReadOnlyList<Trend> Trends(IEnumerable<Trend> trends) =>
        [.. trends.Select(trend => new Trend(trend.Code, Name(trend.Code, trend.Title), Unit(trend.Unit), trend.Low, trend.High, trend.Points, trend.Kind))];
}
