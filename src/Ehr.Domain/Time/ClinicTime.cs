using System.Globalization;

namespace Ehr.Domain.Time;

// The clinic's time zone (the expansion plan's step 10d, Tahseen's decision of 19 November 2026). Clinician pages and
// the desktop app show times in it, labeled, and take times typed in it; the portal shows them in it too (Tahseen's
// decision of 26 November 2026). Everything is still stored in UTC, and the audit log is still shown in UTC.
//
// One zone, from configuration: Ehr:Clinic:TimeZone, an IANA name such as "America/Chicago". Without it, in
// Development, the zone is UTC and the label "UTC", which is how every page read before this step. The label after a
// time is the zone's abbreviation when Ehr:Clinic:TimeZoneAbbreviations gives the two ("CST,CDT": standard, daylight),
// and otherwise the offset from UTC at that moment ("UTC-6"): .NET has no abbreviations that are the same on Linux,
// macOS and Windows, so they are configured, not looked up.
public sealed class ClinicTime
{
    readonly string? standard;

    readonly string? daylight;

    public ClinicTime(string? timeZone, string? abbreviations = null)
    {
        Id = string.IsNullOrWhiteSpace(timeZone) ? "UTC" : timeZone.Trim();
        try
        {
            Zone = Id == "UTC" ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(Id);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException(
                $"Ehr:Clinic:TimeZone is \"{Id}\", which isn't a time zone this server knows. Use an IANA name, such as America/Chicago.", e);
        }

        // The desktop app is sent this name and can only use an IANA one: a Windows name the server happens to know
        // ("Central Standard Time") is turned into its IANA name, or refused.
        if (Id != "UTC" && !Zone.HasIanaId)
        {
            Id = TimeZoneInfo.TryConvertWindowsIdToIanaId(Zone.Id, out var iana)
                ? iana
                : throw new InvalidOperationException($"Ehr:Clinic:TimeZone is \"{Id}\", which isn't an IANA name. Use one, such as America/Chicago.");
        }

        var parts = abbreviations?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (parts.Length is not (0 or 2) || parts.Any(part => part.Length > 6))
        {
            throw new InvalidOperationException(
                "Ehr:Clinic:TimeZoneAbbreviations takes two short names, the zone's standard and daylight ones, such as \"CST,CDT\".");
        }

        (standard, daylight) = parts.Length == 2 ? (parts[0], parts[1]) : (null, null);
    }

    // The zone's IANA name, which the desktop app is given to show its offline copy's times with.
    public string Id { get; }

    public TimeZoneInfo Zone { get; }

    public bool IsUtc => Zone.BaseUtcOffset == TimeSpan.Zero && !Zone.SupportsDaylightSavingTime;

    // The setting, checked. Outside Development the zone must be said: a clinic's times are never UTC by accident.
    public static ClinicTime FromSettings(string? timeZone, string? abbreviations, bool development) =>
        !development && string.IsNullOrWhiteSpace(timeZone)
            ? throw new InvalidOperationException(
                "Set Ehr:Clinic:TimeZone to the clinic's time zone, an IANA name such as America/Chicago (Ehr__Clinic__TimeZone in the environment). "
                + "Clinician pages show and take times in it.")
            : new ClinicTime(timeZone, abbreviations);

    // A moment as the clinic's clock shows it.
    public DateTimeOffset At(DateTimeOffset moment) => TimeZoneInfo.ConvertTime(moment, Zone);

    public DateTimeOffset Now => At(DateTimeOffset.UtcNow);

    public DateOnly Today => DateOnly.FromDateTime(Now.DateTime);

    // What follows a time on a page: "UTC", the zone's abbreviation at that moment, or its offset ("UTC-6", "UTC+5:30").
    public string Label(DateTimeOffset moment)
    {
        if (IsUtc)
        {
            return "UTC";
        }

        if (standard is not null)
        {
            return Zone.IsDaylightSavingTime(moment) ? daylight! : standard;
        }

        var offset = Zone.GetUtcOffset(moment);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var hours = Math.Abs(offset.Hours).ToString(CultureInfo.InvariantCulture);
        return offset.Minutes == 0 ? $"UTC{sign}{hours}" : $"UTC{sign}{hours}:{Math.Abs(offset.Minutes):00}";
    }

    // "2026-11-15 14:05 CST": the clinic's clock, with the label. The brand guide's form for clinician pages.
    public string Format(DateTimeOffset moment, string format = "yyyy-MM-dd HH:mm") =>
        $"{At(moment).ToString(format, CultureInfo.InvariantCulture)} {Label(moment)}";

    public string? Format(DateTimeOffset? moment, string format = "yyyy-MM-dd HH:mm") => moment is { } at ? Format(at, format) : null;

    // The clinic's clock with no label, where a label is already beside it (a table's heading, a field's label).
    public string Plain(DateTimeOffset moment, string format = "yyyy-MM-dd HH:mm") => At(moment).ToString(format, CultureInfo.InvariantCulture);

    // The clinic's calendar day a moment falls on: "2026-11-15".
    public string Day(DateTimeOffset moment) => At(moment).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // The moment a clinic clock reading names. Null when that reading doesn't exist: the hour skipped when the clocks
    // go forward. A reading that happens twice, when they go back, is taken as the first of the two (daylight time).
    public DateTimeOffset? From(DateTime clock)
    {
        clock = DateTime.SpecifyKind(clock, DateTimeKind.Unspecified);
        if (Zone.IsInvalidTime(clock))
        {
            return null;
        }

        var offset = Zone.IsAmbiguousTime(clock) ? Zone.GetAmbiguousTimeOffsets(clock).Max() : Zone.GetUtcOffset(clock);
        return new DateTimeOffset(clock, offset).ToUniversalTime();
    }

    // A clinic calendar day as the moments it runs between: from its midnight up to, not including, the next one's.
    // A day the clocks change on is 23 or 25 hours long.
    public (DateTimeOffset From, DateTimeOffset To) DayBounds(DateOnly day) => (StartOf(day), StartOf(day.AddDays(1)));

    DateTimeOffset StartOf(DateOnly day)
    {
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        // Midnight itself is skipped in a few zones when the clocks go forward: the day starts when the clock next reads.
        while (Zone.IsInvalidTime(midnight))
        {
            midnight = midnight.AddMinutes(30);
        }

        return From(midnight)!.Value;
    }

    // Where a field's label, or a table's heading, says which clock: "UTC" or "clinic time". No abbreviation: the
    // time typed may be on the other side of a clock change from now.
    public string ClockName => IsUtc ? "UTC" : "clinic time";

    // "09:30 to 09:50 CST"; with each end's own label when the clocks change between them: "01:50 CDT to 01:10 CST".
    public string Range(DateTimeOffset from, DateTimeOffset to, string format = "HH:mm") =>
        Label(from) == Label(to) ? $"{Plain(from, format)} to {Format(to, format)}" : $"{Format(from, format)} to {Format(to, format)}";
}
