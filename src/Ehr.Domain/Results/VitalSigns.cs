using System.Globalization;
using Ehr.Data;
using Ehr.Data.Patients;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ehr.Domain.Results;

// Recording a set of vital signs (the expansion plan's step 9): blood pressure, heart rate, temperature, respiratory
// rate, oxygen saturation, weight and height, taken at one moment, by a clinician with the patient on their panel.
// Each is an observation with its LOINC code. This class checks what was typed and sends the rows; the database sets
// each one's name, unit and reference range, works out the body mass index from the weight and height, and decides who
// may (db/policies/30-rls.sql and 75-results.sql). It reads nothing: the flowsheet comes from the PHI reader.
public sealed class VitalSigns(EhrDbContext db)
{
    // What the form asks for, in its order, with the label and unit shown beside each field and the least and most
    // the field takes: the widest a person can have, the same bounds the database holds (75-results.sql). The body
    // mass index is not asked for.
    public static readonly IReadOnlyList<(string Code, string Label, string Unit, decimal Least, decimal Most)> Fields =
    [
        (VitalSign.Systolic, "Systolic blood pressure", "mmHg", 1, 350), (VitalSign.Diastolic, "Diastolic blood pressure", "mmHg", 1, 250),
        (VitalSign.HeartRate, "Heart rate", "per minute", 1, 350), (VitalSign.Temperature, "Temperature", "°C", 20, 45),
        (VitalSign.RespiratoryRate, "Respiratory rate", "per minute", 1, 100), (VitalSign.OxygenSaturation, "Oxygen saturation", "%", 1, 100),
        (VitalSign.Weight, "Weight", "kg", 0.2m, 700), (VitalSign.Height, "Height", "cm", 20, 280),
    ];

    // A sign's short name, as the form labels it, where a list has no room for the coded one.
    public static string Name(string code, string coded) =>
        code == VitalSign.BodyMassIndex ? "Body mass index" : Fields.FirstOrDefault(field => field.Code == code).Label ?? coded;

    // Marks vital signs "entered in error" (step 9b), all or none, with the reason. The rows are never deleted; the
    // correct values are recorded as a new set. The database decides who may (a clinician with the patient on their
    // panel), allows only a final vital sign to be marked, sets who and when, and marks the body mass index worked out
    // from a weight or height marked here, and the other value of a blood pressure reading (step 9c), so `ids` names
    // one value of each reading (db/policies/30-rls.sql and 75-results.sql). `ids` come from a read of this patient's
    // vital signs: the caller has checked they are the patient's.
    public async Task<Marked> MarkInErrorAsync(IReadOnlyCollection<Guid> ids, string reason, CancellationToken cancellationToken)
    {
        const string savepoint = "vital_signs_in_error";
        var transaction = db.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync(savepoint, cancellationToken);
        var rows = ids.Select(id => (object)new Observation { Id = id, Display = "", Status = ObservationStatus.EnteredInError, EnteredInErrorReason = reason }).ToArray();
        try
        {
            foreach (var row in rows)
            {
                // Only the two columns the apps may update.
                var entry = db.Attach(row);
                entry.Property(nameof(Observation.Status)).IsModified = true;
                entry.Property(nameof(Observation.EnteredInErrorReason)).IsModified = true;
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.ReleaseSavepointAsync(savepoint, cancellationToken);
            return Marked.Done;
        }
        catch (Exception e) when (e is DbUpdateConcurrencyException
            || e is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.CheckViolation } })
        {
            // No row updated: not on the panel (row-level security), or already marked.
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return Marked.Refused;
        }
        finally
        {
            foreach (var row in rows)
            {
                db.Entry(row).State = EntityState.Detached;
            }
        }
    }

    // What is wrong with what was typed, by code ("" for the whole form, "TakenAt" for the time): empty when it can be sent.
    public static Dictionary<string, string> Check(IReadOnlyDictionary<string, string?> typed, DateTimeOffset? takenAt, out List<(string Code, decimal Value)> values)
    {
        var errors = new Dictionary<string, string>();
        values = [];
        foreach (var (code, _, unit, least, most) in Fields)
        {
            if (!typed.TryGetValue(code, out var text) || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (!decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || decimal.Round(value, 1) != value)
            {
                errors[code] = "Enter a number, like 36.8.";
            }
            else if (value < least || value > most)
            {
                errors[code] = $"Enter a value from {least:0.#} to {most:0.#} {unit}.";
            }
            else
            {
                values.Add((code, value));
            }
        }

        var entered = values.Select(value => value.Code).ToHashSet();
        if (entered.Contains(VitalSign.Systolic) != entered.Contains(VitalSign.Diastolic) && !errors.ContainsKey(VitalSign.Systolic) && !errors.ContainsKey(VitalSign.Diastolic))
        {
            errors[entered.Contains(VitalSign.Systolic) ? VitalSign.Diastolic : VitalSign.Systolic] = "Enter both numbers of the blood pressure.";
        }

        if (errors.Count == 0 && values.Count == 0)
        {
            errors[""] = "Enter at least one vital sign.";
        }

        if (takenAt is null)
        {
            errors["TakenAt"] = "Enter when they were taken.";
        }
        else if (takenAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            errors["TakenAt"] = "The time can't be in the future.";
        }

        return errors;
    }

    // Records the set, all or none. Refused when the database said no: the patient isn't on the signed-in clinician's
    // panel, or a value is one it doesn't take. AlreadyRecorded when the same sign is already there for the same
    // moment: a form sent twice records once.
    // `bodyMassIndex`: whether to record one, when the caller knows better than "a weight and a height are among
    // the values" (a corrected weight sent beside a height that already stands).
    public async Task<Recorded> RecordAsync(
        Guid patient, IReadOnlyList<(string Code, decimal Value)> values, DateTimeOffset takenAt, CancellationToken cancellationToken, bool? bodyMassIndex = null)
    {
        // To the second: the signs of one set share their time, which is how the flowsheet puts them in one column.
        var at = new DateTimeOffset(takenAt.UtcDateTime.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
        var codes = values.Select(value => value.Code).ToList();
        // With a weight and a height, the body mass index too, last: the database works its value out from the two.
        var rows = values.Concat(bodyMassIndex ?? (codes.Contains(VitalSign.Weight) && codes.Contains(VitalSign.Height)) ? [(VitalSign.BodyMassIndex, 0m)] : [])
            .Select(value => (object)new Observation
            {
                Id = Guid.CreateVersion7(),
                PatientId = patient,
                Category = ObservationCategory.VitalSigns,
                Code = value.Item1,
                Display = value.Item1,
                Status = ObservationStatus.Final,
                Value = value.Item2,
                EffectiveAt = at,
            }).ToArray();
        const string savepoint = "vital_signs";
        var transaction = db.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync(savepoint, cancellationToken);
        try
        {
            // One at a time, in order: the body mass index is worked out from rows already there.
            foreach (var row in rows)
            {
                db.Add(row);
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.ReleaseSavepointAsync(savepoint, cancellationToken);
            return Recorded.Saved;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.UniqueViolation
                or PostgresErrorCodes.NumericValueOutOfRange,
        } refusal)
        {
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return refusal.SqlState == PostgresErrorCodes.UniqueViolation ? Recorded.AlreadyRecorded : Recorded.Refused;
        }
        finally
        {
            foreach (var row in rows)
            {
                db.Entry(row).State = EntityState.Detached;
            }
        }
    }
}

public enum Marked
{
    Done,
    Refused,
}

public enum Recorded
{
    Saved,
    Refused,
    AlreadyRecorded,
}

// One value of a test or a vital sign at a time.
// With the reference range the value was recorded against: a vital sign's goes by the patient's age on the day it
// was taken (step 9b), so one chart's points can have different ranges, and a child's blood pressure is a screening
// value (step 9d).
public sealed record TrendPoint(DateTimeOffset At, decimal Value, decimal? Low = null, decimal? High = null, string Kind = ReferenceKind.Normal)
{
    // "High", "Low" or "Needs follow-up" (VitalFlag), or null.
    public string? Flag => VitalFlag.Of(Value, Low, High, Kind);

    public bool IsOutside => Flag is not null;
}

// The values of one test or vital sign over time, with where each sits on a small chart (step 9). The chart is drawn
// by the server as SVG, with no script, and its numbers are also given as a table: the chart is never the only way
// to read them. Coordinates are in the chart's own units, 320 by 120.
public sealed class Trend
{
    public const int Width = 320;
    public const int Height = 120;
    const decimal Left = 10, Right = 10, Top = 12, Bottom = 12;

    // The most points a chart shows: the latest ones.
    public const int Most = 24;

    public Trend(string code, string title, string? unit, decimal? low, decimal? high, IEnumerable<TrendPoint> points, string kind = ReferenceKind.Normal)
    {
        (Code, Title, Unit, Low, High, Kind) = (code, title, Units.Shown(unit), low, high, kind);
        Points = [.. points.OrderBy(point => point.At).TakeLast(Most)];
        var values = Points.Select(point => point.Value).Concat(new[] { low, high }.Where(bound => bound is not null).Select(bound => bound!.Value)).ToList();
        (Floor, Ceiling) = (values.Min(), values.Max());
        if (Floor == Ceiling)
        {
            (Floor, Ceiling) = (Floor - 1, Ceiling + 1);
        }
    }

    public string Code { get; }

    public string Title { get; }

    // The unit as a person reads it.
    public string? Unit { get; }

    public decimal? Low { get; }

    public decimal? High { get; }

    // The latest point's kind of range (ReferenceKind): a screening value is shown as one, and needs follow-up at or
    // above it.
    public string Kind { get; }

    public bool IsScreening => Kind == ReferenceKind.Screening;

    // Oldest first.
    public IReadOnlyList<TrendPoint> Points { get; }

    // The lowest and highest value the chart spans: its values and its reference range.
    public decimal Floor { get; }

    public decimal Ceiling { get; }

    public bool HasRange => Low is not null || High is not null;

    // Each point against the range it was recorded with, not the latest one.
    public static bool IsOutside(TrendPoint point) => point.IsOutside;

    // Whether the points were recorded against more than one range: a child who grew into another age group.
    public bool RangesDiffer => Points.Select(point => (point.Low, point.High, point.Kind)).Distinct().Count() > 1;

    public decimal X(int index) => Points.Count == 1 ? Width / 2m : Left + (index * (Width - Left - Right) / (Points.Count - 1));

    public decimal Y(decimal value) => Top + ((Ceiling - value) * (Height - Top - Bottom) / (Ceiling - Floor));

    // The line through the points, as an SVG polyline's points.
    public string Line => string.Join(' ', Points.Select((point, index) => $"{Number(X(index))},{Number(Y(point.Value))}"));

    // The band of the reference range: its top edge and its height.
    public (string Y, string Height) Band
    {
        get
        {
            var (top, bottom) = (Y(High ?? Ceiling), Y(Low ?? Floor));
            return (Number(top), Number(bottom - top));
        }
    }

    public string RangeText => (Low, High) switch
    {
        (not null, not null) => $"{Low:0.####} to {High:0.####}",
        (not null, null) => $"at least {Low:0.####}",
        (null, not null) => $"at most {High:0.####}",
        _ => "",
    };

    // What the chart shows, in a sentence, for a reader who can't see it.
    public string Summary => SummaryOn(at => at.UtcDateTime.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture));

    // The same sentence with its two days as the clinic's calendar has them (step 10d), for clinician pages and the portal.
    public string SummaryIn(Ehr.Domain.Time.ClinicTime clinic) => SummaryOn(at => clinic.Plain(at, "MMM d, yyyy"));

    string SummaryOn(Func<DateTimeOffset, string> day)
    {
        var (first, last) = (Points[0], Points[^1]);
        var change = last.Value == first.Value ? "the same as" : last.Value > first.Value ? "up from" : "down from";
        return $"{Title}, {Points.Count} values from {day(first.At)} to {day(last.At)}. "
            + $"Latest {last.Value:0.####}{(Unit is null ? "" : " " + Unit)}, {change} {first.Value:0.####}."
            + (HasRange ? IsScreening ? $" Needs follow-up at or above {High:0.####}{(Unit is null ? "" : " " + Unit)}." : $" Reference range {RangeText}." : "")
            + (RangesDiffer ? " That range is for the latest reading. Earlier ones are marked against the range for the patient's age then." : "");
    }

    // Always with a point for the decimal, whatever the server's language: these go into SVG attributes.
    public static string Number(decimal value) => decimal.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture);

    // One trend for each test or vital sign with at least two numeric values, a vital sign titled as the flowsheet's
    // row names it (VitalSigns.Name), in the order given by `order` when there is one, by name otherwise.
    public static IReadOnlyList<Trend> From(IEnumerable<Observation> observations, IReadOnlyList<string>? order = null)
    {
        // Never a value marked entered in error (step 9b), or one not finished.
        var trends = observations.Where(o => o.Code is not null && o.Value is not null && ObservationStatus.Released.Contains(o.Status))
            .GroupBy(o => o.Code!)
            .Where(group => group.Count() >= 2)
            .Select(group =>
            {
                var latest = group.OrderByDescending(o => o.EffectiveAt).ThenByDescending(o => o.IssuedAt).First();
                // Two values of one moment stay in the order they were released (the chart's own sort keeps it).
                return new Trend(group.Key, VitalSigns.Name(group.Key, latest.Display), latest.Unit, latest.ReferenceLow, latest.ReferenceHigh,
                    group.OrderBy(o => o.EffectiveAt).ThenBy(o => o.IssuedAt).ThenBy(o => o.Id)
                        .Select(o => new TrendPoint(o.EffectiveAt, o.Value!.Value, o.ReferenceLow, o.ReferenceHigh, o.ReferenceKind)), latest.ReferenceKind);
            });
        var places = order?.Select((code, place) => (code, place)).ToDictionary(item => item.code, item => item.place);
        return [.. places is null
            ? trends.OrderBy(trend => trend.Title, StringComparer.OrdinalIgnoreCase)
            : trends.OrderBy(trend => places.GetValueOrDefault(trend.Code, int.MaxValue))];
    }
}

// A unit as a person reads it. The record keeps UCUM's spelling ("mm[Hg]", "Cel"), which is for machines; the few
// whose spelling a reader would stumble on are shown the usual way, and every other unit as it is.
public static class Units
{
    public static string? Shown(string? ucum) => ucum switch
    {
        "mm[Hg]" => "mmHg",
        "Cel" => "°C",
        "kg/m2" => "kg/m²",
        "/min" => "per minute",
        _ => ucum,
    };

    // For patients (Tahseen's decision of 26 November 2026), in the portal's names, trends, results and printable record:
    // UCUM's print form for the units whose code reads badly to a patient, and any other as Shown gives it.
    public static string? ForPatients(string? ucum) => ucum switch
    {
        "10*3/uL" => "10³/µL",
        "10*6/uL" => "10⁶/µL",
        "m[IU]/L" => "mIU/L",
        _ => Shown(ucum),
    };
}

// A patient's vital signs for the flowsheet page: every one recorded (the latest first, up to a limit), already
// recorded as one read of the patient.
public sealed record PatientVitals(Patient Patient, IReadOnlyList<AllergyIntolerance> Allergies, IReadOnlyList<Observation> Signs);
