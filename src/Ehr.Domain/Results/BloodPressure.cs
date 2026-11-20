using Ehr.Data.Patients;

namespace Ehr.Domain.Results;

// A blood pressure reading (the expansion plan's step 9c): a systolic and a diastolic value that share a reading ID,
// which the database gives them when they are entered together and holds standing together or entered in error
// together (db/policies/75-results.sql). Every page that shows a blood pressure takes its two numbers from one reading,
// never by matching times, and shows it as one value, "126/80", flagged when either number is outside its range.
public sealed record BloodPressure(Observation Systolic, Observation Diastolic)
{
    public DateTimeOffset TakenAt => Systolic.EffectiveAt;

    public bool Stands => ObservationStatus.Released.Contains(Systolic.Status) && ObservationStatus.Released.Contains(Diastolic.Status);

    public string Text => $"{Systolic.Value:0.#}/{Diastolic.Value:0.#}";

    public bool IsHigh => Systolic.Value > Systolic.ReferenceHigh || Diastolic.Value > Diastolic.ReferenceHigh;

    public bool IsLow => Systolic.Value < Systolic.ReferenceLow || Diastolic.Value < Diastolic.ReferenceLow;

    // The readings among these values that have both of theirs here, the latest first.
    public static IReadOnlyList<BloodPressure> Readings(IEnumerable<Observation> values) =>
        [.. values.Where(value => value.ReadingId is not null && value.Value is not null && VitalSign.BloodPressure.Contains(value.Code))
            .GroupBy(value => value.ReadingId!.Value)
            .Select(reading => (Systolic: reading.FirstOrDefault(value => value.Code == VitalSign.Systolic), Diastolic: reading.FirstOrDefault(value => value.Code == VitalSign.Diastolic)))
            .Where(reading => reading.Systolic is not null && reading.Diastolic is not null)
            .Select(reading => new BloodPressure(reading.Systolic!, reading.Diastolic!))
            .OrderByDescending(reading => reading.TakenAt).ThenByDescending(reading => reading.Systolic.IssuedAt)];

    // These values, with a blood pressure value kept only when the other value of its reading is here too: a trend
    // charts each reading's two numbers, or neither.
    public static IReadOnlyList<Observation> Whole(IReadOnlyCollection<Observation> values)
    {
        var whole = Readings(values).SelectMany(reading => new[] { reading.Systolic.Id, reading.Diastolic.Id }).ToHashSet();
        return [.. values.Where(value => !VitalSign.BloodPressure.Contains(value.Code) || whole.Contains(value.Id))];
    }
}
