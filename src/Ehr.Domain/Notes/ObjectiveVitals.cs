using System.Globalization;
using Ehr.Data.Patients;
using Ehr.Domain.Results;
using Ehr.Domain.Time;

namespace Ehr.Domain.Notes;

// The vital signs a clinician may add to a note's Objective (the expansion plan's step 14), as text: those of the latest
// clinic day on which the patient has a vital sign standing, the newest of each sign that day, a blood pressure as one
// reading ("126/80", step 9c), none entered in error (step 9b), each with the time it was taken on the clinic's clock,
// labeled. Offered only when the clinician asks; the page adds the text to Objective only when they choose to, and the
// note keeps nothing but that text. The day is the clinic's calendar day of each moment (ClinicTime), so a value taken
// in the hour the clocks repeat when they go back is on that day either way, and its label says which of the two hours.
// Day is null and Text empty when the patient has none.
public sealed record ObjectiveVitals(string? Day, bool Today, string Text)
{
    public static ObjectiveVitals Of(IReadOnlyList<Observation> standing, ClinicTime clinic)
    {
        if (standing.Count == 0)
        {
            return new(null, false, "");
        }

        var latest = standing.MaxBy(sign => sign.EffectiveAt)!.EffectiveAt;
        var day = clinic.Day(latest);
        var thatDay = standing.Where(sign => clinic.Day(sign.EffectiveAt) == day).ToList();
        var values = new List<(string Text, DateTimeOffset TakenAt)>();
        if (BloodPressure.Readings(thatDay).FirstOrDefault() is { } pressure)
        {
            values.Add((Shown("blood pressure", pressure.Text, pressure.Systolic.Unit), pressure.TakenAt));
        }

        foreach (var code in VitalSign.All.Except(VitalSign.BloodPressure))
        {
            if (thatDay.Where(sign => sign.Code == code).OrderByDescending(sign => sign.EffectiveAt).ThenByDescending(sign => sign.IssuedAt).FirstOrDefault() is { } sign)
            {
                var name = VitalSigns.Name(code, sign.Display);
                values.Add((Shown(char.ToLowerInvariant(name[0]) + name[1..], sign.Value!.Value.ToString("0.#", CultureInfo.InvariantCulture), sign.Unit), sign.EffectiveAt));
            }
        }

        // One time for the set when every value shares it; otherwise each value's own.
        var shown = clinic.Plain(latest, "MMM d, yyyy");
        var text = values.Select(value => value.TakenAt).Distinct().Count() == 1
            ? $"Vital signs, {clinic.Format(values[0].TakenAt)}: {string.Join(", ", values.Select(value => value.Text))}."
            : $"Vital signs, {shown}: {string.Join("; ", values.Select(value => $"{value.Text} at {clinic.Format(value.TakenAt, "HH:mm")}"))}.";
        return new(shown, day == clinic.Day(clinic.Now), text);
    }

    static string Shown(string name, string value, string? unit) =>
        Units.Shown(unit) is { } shown ? $"{name} {value} {shown}" : $"{name} {value}";
}
