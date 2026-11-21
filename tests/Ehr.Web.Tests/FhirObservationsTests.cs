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

    // A child's blood pressure has a screening value, not a normal range (step 9d): the API says so in words and
    // sends no high, which a reader would take for the top of a normal range. An adult's range is sent as numbers.
    [Fact]
    public void A_screening_value_is_sent_in_words_and_never_as_the_top_of_a_normal_range()
    {
        var (child, adult) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        ObservationEntry Ranged(string code, decimal value, Guid reading, decimal? low, decimal? high, string kind)
        {
            var entry = Value(code, value, reading);
            entry.Observation.ReferenceLow = low;
            entry.Observation.ReferenceHigh = high;
            entry.Observation.ReferenceKind = kind;
            return entry;
        }

        var mapped = FhirObservations.Map(
        [
            Ranged(VitalSign.Systolic, 98, child, null, 98, ReferenceKind.Screening) with { Diastolic = Ranged(VitalSign.Diastolic, 50, child, null, 54, ReferenceKind.Screening) },
            Ranged(VitalSign.Systolic, 118, adult, 90, 129, ReferenceKind.Normal) with { Diastolic = Ranged(VitalSign.Diastolic, 76, adult, 60, 79, ReferenceKind.Normal) },
        ]);

        var screened = mapped[0].Resource.Component!.Select(component => Assert.Single(component.ReferenceRange!)).ToList();
        Assert.All(screened, range => Assert.Null(range.Low));
        Assert.All(screened, range => Assert.Null(range.High));
        Assert.Equal(
            ["Screening value: needs follow-up at or above 98 mm[Hg].", "Screening value: needs follow-up at or above 54 mm[Hg]."],
            screened.Select(range => range.Text));
        var normal = mapped[1].Resource.Component!.Select(component => Assert.Single(component.ReferenceRange!)).ToList();
        Assert.Equal([(90m, 129m), (60m, 79m)], normal.Select(range => (range.Low!.Value, range.High!.Value)));
        Assert.All(normal, range => Assert.Null(range.Text));

        // Both are valid R4 blood pressure Observations as the API writes them.
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        Assert.All(mapped, observation => FhirConformance.Valid<Hl7.Fhir.Model.Observation>(System.Text.Json.JsonSerializer.Serialize(observation.Resource, observation.Resource.GetType(), json)));
    }
}
