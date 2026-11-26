using Ehr.Data.Patients;
using Ehr.Domain.Notes;
using Ehr.Domain.Time;
using Xunit;

namespace Ehr.Web.Tests;

// The vital signs offered for a note's Objective (the expansion plan's step 14, Notes.ObjectiveVitals), worked out from
// values as the PHI reader gives them, on a clinic's clock: the latest clinic day's, the newest of each sign that day,
// a blood pressure as one reading. TC-UC14-30 asks the page for them.
public class ObjectiveVitalsTests
{
    static readonly ClinicTime Chicago = new("America/Chicago", "CST,CDT");

    static Observation Sign(string code, decimal value, string unit, string takenAt, Guid? reading = null) => new()
    {
        Id = Guid.CreateVersion7(),
        Code = code,
        Display = code,
        Value = value,
        Unit = unit,
        Status = ObservationStatus.Final,
        Category = ObservationCategory.VitalSigns,
        EffectiveAt = DateTimeOffset.Parse(takenAt, System.Globalization.CultureInfo.InvariantCulture),
        ReadingId = reading,
    };

    // When the clocks go back, 01:30 happens twice. Each moment's clinic day is its calendar day on the clinic's clock, so
    // both are on that day, and each time carries its own label, which says which of the two hours it was. The clinic's
    // evening before, though later in UTC than midnight there, is another day.
    [Fact]
    public void Values_in_the_hour_the_clocks_repeat_are_on_that_day_each_with_its_own_label()
    {
        var offer = ObjectiveVitals.Of(
        [
            Sign(VitalSign.HeartRate, 75, "/min", "2026-11-01T07:30:00Z"),
            Sign(VitalSign.HeartRate, 70, "/min", "2026-11-01T06:30:00Z"),
            Sign(VitalSign.Temperature, 37.1m, "Cel", "2026-11-01T06:30:00Z"),
            Sign(VitalSign.Weight, 80, "kg", "2026-11-01T04:30:00Z"),
        ], Chicago);

        Assert.Equal("2026-11-01", offer.Day);
        Assert.Equal("Vital signs, 2026-11-01: heart rate 75 per minute at 01:30 CST; temperature 37.1 °C at 01:30 CDT.", offer.Text);
    }

    // A blood pressure is one reading's two numbers (step 9c), and a set taken at one moment gives its time once.
    [Fact]
    public void A_set_gives_its_time_once_and_a_blood_pressure_as_one_reading()
    {
        var reading = Guid.CreateVersion7();
        var offer = ObjectiveVitals.Of(
        [
            Sign(VitalSign.Systolic, 126, "mm[Hg]", "2026-11-20T16:05:00Z", reading),
            Sign(VitalSign.Diastolic, 80, "mm[Hg]", "2026-11-20T16:05:00Z", reading),
            Sign(VitalSign.OxygenSaturation, 98, "%", "2026-11-20T16:05:00Z"),
        ], Chicago);

        Assert.Equal("Vital signs, 2026-11-20 10:05 CST: blood pressure 126/80 mmHg, oxygen saturation 98 %.", offer.Text);
        Assert.False(offer.Today);
        Assert.Equal(new ObjectiveVitals(null, false, ""), ObjectiveVitals.Of([], Chicago));
    }
}
