using System.Globalization;
using Ehr.Data.Patients;

namespace Ehr.Domain.Results;

// Puts a test result in plain language for the patient portal (FR-16, UC6; Simar Rekhi): whether the value is below,
// within or above its normal range, a sentence that says so, and what the test measures, for the common tests a
// primary care clinic orders. No AI and no guessing: a test this doesn't know gets only the range sentence, and a
// result with no range or no number says to ask the care team. Nothing here reads the database.
public static class ResultExplanation
{
    // What common tests measure, by LOINC code, in words a patient knows.
    static readonly Dictionary<string, string> Measures = new()
    {
        ["4548-4"] = "your average blood sugar over the past 2 to 3 months",
        ["2345-7"] = "the amount of sugar in your blood when the sample was taken",
        ["2093-3"] = "the total amount of cholesterol, a kind of fat, in your blood",
        ["13457-7"] = "LDL, the \"bad\" cholesterol that can build up in your blood vessels",
        ["18262-6"] = "LDL, the \"bad\" cholesterol that can build up in your blood vessels",
        ["2085-9"] = "HDL, the \"good\" cholesterol that helps clear other cholesterol from your blood",
        ["2571-8"] = "triglycerides, a kind of fat in your blood that comes from food",
        ["718-7"] = "hemoglobin, the part of your red blood cells that carries oxygen",
        ["6690-2"] = "white blood cells, which fight infection",
        ["2160-0"] = "creatinine, a waste product your kidneys filter out, which shows how well they work",
        ["2823-3"] = "potassium, a mineral your heart and muscles need to work properly",
        ["2951-2"] = "sodium, a mineral that keeps the fluids in your body in balance",
        ["3016-3"] = "thyroid stimulating hormone (TSH), which shows how well your thyroid is working",
        ["1989-3"] = "vitamin D, which helps keep your bones strong",
    };

    // Below, within or above the normal range, or None when there's no number or no range to compare it with.
    public static ResultFlag Flag(Observation result) => result switch
    {
        { Value: null } => ResultFlag.None,
        { ReferenceLow: null, ReferenceHigh: null } => ResultFlag.None,
        { ReferenceLow: { } low } when result.Value < low => ResultFlag.Low,
        { ReferenceHigh: { } high } when result.Value > high => ResultFlag.High,
        _ => ResultFlag.Normal,
    };

    // "This test measures ...", or null for a test not in the list.
    public static string? WhatItMeasures(Observation result) =>
        result.Code is { } code && Measures.TryGetValue(code, out var measures) ? $"This test measures {measures}." : null;

    // The result as it is shown, such as "6.9 %".
    public static string? Value(Observation result) =>
        result.Value is { } value ? Join(Number(value), result.Unit) : null;

    // The normal range as people say it: "4.0 to 5.6 %", "below 100 mg/dL", "above 40 mg/dL", or null without one.
    public static string? Range(Observation result) => (result.ReferenceLow, result.ReferenceHigh) switch
    {
        ({ } low, { } high) => Join($"{Number(low)} to {Number(high)}", result.Unit),
        (null, { } high) => Join($"below {Number(high)}", result.Unit),
        ({ } low, null) => Join($"above {Number(low)}", result.Unit),
        _ => null,
    };

    // The one sentence that says what the result means against its range.
    public static string Meaning(Observation result) => Flag(result) switch
    {
        ResultFlag.High => $"Your result is above the normal range ({Range(result)}).",
        ResultFlag.Low => $"Your result is below the normal range ({Range(result)}).",
        ResultFlag.Normal => $"Your result is in the normal range ({Range(result)}).",
        _ when result.Value is null => "This result has no number to compare. Ask your care team what it means.",
        _ => "This test has no normal range to compare with. Ask your care team what it means.",
    };

    // Up to four decimal places, as stored, without trailing zeros: 6.9, not 6.9000.
    static string Number(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    // With the unit as patients read it everywhere in the portal (Units.ForPatients): "7.2 10³/µL", not "10*3/uL".
    static string Join(string number, string? unit) => Units.ForPatients(unit) is { Length: > 0 } shown ? $"{number} {shown}" : number;
}

public enum ResultFlag
{
    None,
    Low,
    Normal,
    High,
}
