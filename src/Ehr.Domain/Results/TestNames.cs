namespace Ehr.Domain.Results;

// How a page names a test or a vital sign to a person, in the portal (Tahseen's decision of 26 November 2026) and on
// clinician pages (his decision of 27 November 2026) alike. The reference data has no shorter name for a test
// (ehr.observation_codes holds LOINC's long one), so a test is named by its long name's part before the first " [":
// "Hematocrit" for "Hematocrit [Volume Fraction] of Blood by Automated count"; a name without one stays whole. A vital
// sign is named as the clinic's form labels it (VitalSigns.Name): "Oxygen saturation". The record keeps LOINC's names,
// the FHIR file and the FHIR API give them unchanged, and a page that names a test shortly still gives the long name:
// under the result in the portal, in the name's title on clinician pages.
public static class TestNames
{
    public static string Short(string? code, string display)
    {
        var name = VitalSigns.Name(code ?? "", display);
        var bracket = name.IndexOf(" [", StringComparison.Ordinal);
        return bracket > 0 ? name[..bracket] : name;
    }

    // The long name where the short one leaves part of it out, for the short one's title; otherwise null.
    public static string? Full(string? code, string display) => Short(code, display) == display ? null : display;
}
