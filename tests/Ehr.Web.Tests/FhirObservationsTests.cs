using Ehr.Data.Patients;
using Ehr.Domain.Phi;
using Ehr.Web.Api.Fhir;
using Xunit;

namespace Ehr.Web.Tests;

// How the FHIR API makes Observations of what the PHI reader read (FhirObservations), with no database: a blood
// pressure is one Observation for its reading, with both values as components, or nothing when a value of the reading
// is missing, never one value sent alone under its own ID. The database keeps readings whole, so the second can't come
// from it; this is the guard behind that. A blood pressure code on a row with no reading, a laboratory row's, is sent
// as itself.
public class FhirObservationsTests
{
    static readonly Guid Patient = Guid.CreateVersion7();

    static readonly DateTimeOffset Taken = new(2026, 11, 20, 9, 30, 0, TimeSpan.Zero);

    static ObservationEntry Value(string code, decimal value, Guid? reading = null, string category = ObservationCategory.VitalSigns) => new(
        new Observation
        {
            Id = Guid.CreateVersion7(),
            PatientId = Patient,
            Category = category,
            Code = code,
            Display = code,
            Status = ObservationStatus.Final,
            Value = value,
            Unit = code == VitalSign.HeartRate ? "/min" : "mm[Hg]",
            EffectiveAt = Taken,
            IssuedAt = Taken,
            ReadingId = reading,
        },
        UcumUnit: true);

    [Fact]
    public void A_blood_pressure_is_one_observation_for_its_reading_and_a_reading_missing_a_value_is_left_out()
    {
        var whole = Guid.CreateVersion7();
        var half = Guid.CreateVersion7();
        var heartRate = Value(VitalSign.HeartRate, 72);
        var laboratory = Value(VitalSign.Systolic, 119, category: ObservationCategory.Laboratory);

        var mapped = FhirObservations.Map(
        [
            Value(VitalSign.Systolic, 128, whole) with { Diastolic = Value(VitalSign.Diastolic, 82, whole) },
            // A systolic whose diastolic isn't there, and a diastolic on its own.
            Value(VitalSign.Systolic, 131, half),
            Value(VitalSign.Diastolic, 85, half),
            heartRate,
            laboratory,
        ]);

        Assert.Equal([whole, heartRate.Observation.Id, laboratory.Observation.Id], mapped.Select(observation => observation.Id));
        var reading = mapped[0].Resource;
        Assert.Equal((whole.ToString(), FhirObservations.BloodPressurePanel), (reading.Id, reading.Code.Coding![0].Code));
        Assert.Equal([VitalSign.Systolic, VitalSign.Diastolic], reading.Component!.Select(component => component.Code.Coding![0].Code));
        Assert.Null(mapped[1].Resource.Component);
        // The laboratory systolic, which is no reading's: itself, under its own ID and category, claiming no profile.
        var alone = mapped[2].Resource;
        Assert.Equal((VitalSign.Systolic, ObservationCategory.Laboratory), (alone.Code.Coding![0].Code, alone.Category[0].Coding![0].Code));
        Assert.Null(alone.Meta);
        Assert.Null(alone.Component);
    }
}
