using Ehr.Domain.Results;

namespace Ehr.Web.Areas.Portal.Pages;

// How the portal names a test or a vital sign to a patient, and writes its unit (Tahseen's decision of 26 November
// 2026), in this one place; the unit's forms are Ehr.Domain's (Units.ForPatients), which a result's value and range use
// too (ResultExplanation). The record keeps LOINC's names and UCUM's units, and clinicians, the FHIR file and the FHIR
// API read them unchanged. The reference data has no shorter name for a test (ehr.observation_codes holds LOINC's long
// one), so a test is named by its long name's part before the first " [": "Hematocrit" for "Hematocrit [Volume
// Fraction] of Blood by Automated count"; a name without one stays whole. A vital sign is named as the clinic's form
// labels it (VitalSigns.Name): "Oxygen saturation". The results page still gives the long name, under the result.
public static class ForPatients
{
    public static string Name(string? code, string display)
    {
        var name = VitalSigns.Name(code ?? "", display);
        var bracket = name.IndexOf(" [", StringComparison.Ordinal);
        return bracket > 0 ? name[..bracket] : name;
    }

    // UCUM's print form for the units whose code reads badly to a patient; any other as clinicians see it. The forms are
    // in Ehr.Domain (Units.ForPatients), so a result's value, range and meaning (ResultExplanation) say the same.
    public static string? Unit(string? ucum) => Units.ForPatients(ucum);

    // Trends (step 9) with each one's name and unit as above: the chart's title, its sentence and its table.
    public static IReadOnlyList<Trend> Trends(IEnumerable<Trend> trends) =>
        [.. trends.Select(trend => new Trend(trend.Code, Name(trend.Code, trend.Title), Unit(trend.Unit), trend.Low, trend.High, trend.Points, trend.Kind))];
}
