using Ehr.Domain.Time;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Ehr.Web.Tests;

// The clinic's clock (the expansion plan's step 10d): one configured zone, in which clinician pages show and take
// times, labeled. These test the arithmetic on its own; TC-UC4-23 and TC-UC16-07 test the pages.
public class ClinicTimeTests
{
    public const string ChicagoId = "America/Chicago";

    public static readonly ClinicTime ChicagoClock = new(ChicagoId, "CST,CDT");

    // The on-site app with the clinic in Chicago, as the local stack has it. Every other test app's clinic is in UTC.
    public static WebApplicationFactory<Program> Chicago(WebApps apps) => apps.OnPrem.WithWebHostBuilder(builder => builder
        .UseSetting("Ehr:Clinic:TimeZone", ChicagoId)
        .UseSetting("Ehr:Clinic:TimeZoneAbbreviations", "CST,CDT"));

    static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void A_time_is_shown_on_the_clinics_clock_with_the_zones_abbreviation_for_that_moment()
    {
        Assert.Equal("2026-11-15 14:05 CST", ChicagoClock.Format(Utc(2026, 11, 15, 20, 5)));
        Assert.Equal("2026-07-04 18:30 CDT", ChicagoClock.Format(Utc(2026, 7, 4, 23, 30)));
        // The day is the clinic's: early on the 16th in UTC is still the 15th there.
        Assert.Equal("2026-11-15", ChicagoClock.Day(Utc(2026, 11, 16, 3)));
        Assert.Equal("clinic time, CST", ChicagoClock.FieldLabel(Utc(2026, 11, 15, 20)));
        Assert.Null(ChicagoClock.Format((DateTimeOffset?)null));
    }

    [Fact]
    public void Without_abbreviations_the_label_is_the_offset_and_a_clinic_in_UTC_reads_as_before()
    {
        var plain = new ClinicTime(ChicagoId);
        Assert.Equal("2026-11-15 14:05 UTC-6", plain.Format(Utc(2026, 11, 15, 20, 5)));
        Assert.Equal("2026-07-04 18:30 UTC-5", plain.Format(Utc(2026, 7, 4, 23, 30)));
        Assert.Equal("2026-11-15 20:05 UTC+5:30", new ClinicTime("Asia/Kolkata").Format(Utc(2026, 11, 15, 14, 35)));

        foreach (var utc in new[] { new ClinicTime(null), new ClinicTime("  "), new ClinicTime("UTC") })
        {
            Assert.True(utc.IsUtc);
            Assert.Equal("2026-11-15 20:05 UTC", utc.Format(Utc(2026, 11, 15, 20, 5)));
            Assert.Equal("UTC", utc.FieldLabel(Utc(2026, 11, 15, 20)));
            Assert.Equal(Utc(2026, 11, 15, 9, 30), utc.From(new DateTime(2026, 11, 15, 9, 30, 0)));
        }
    }

    [Fact]
    public void A_typed_time_is_the_clinics_and_the_hours_the_clocks_skip_or_repeat_are_handled()
    {
        Assert.Equal(Utc(2026, 11, 15, 20, 5), ChicagoClock.From(new DateTime(2026, 11, 15, 14, 5, 0)));
        Assert.Equal(Utc(2026, 7, 4, 23, 30), ChicagoClock.From(new DateTime(2026, 7, 4, 18, 30, 0, DateTimeKind.Utc)));
        // 8 March 2026: 02:00 to 03:00 doesn't happen in Chicago.
        Assert.Null(ChicagoClock.From(new DateTime(2026, 3, 8, 2, 30, 0)));
        // 1 November 2026: 01:30 happens twice; the first, in daylight time, is meant.
        Assert.Equal(Utc(2026, 11, 1, 6, 30), ChicagoClock.From(new DateTime(2026, 11, 1, 1, 30, 0)));
        // A moment and back.
        var moment = Utc(2026, 12, 31, 23, 59);
        Assert.Equal(moment, ChicagoClock.From(ChicagoClock.At(moment).DateTime));
    }

    [Fact]
    public void A_clinic_day_runs_from_its_midnight_to_the_next_and_is_shorter_or_longer_when_the_clocks_change()
    {
        Assert.Equal((Utc(2026, 11, 15, 6), Utc(2026, 11, 16, 6)), ChicagoClock.DayBounds(new DateOnly(2026, 11, 15)));
        var (from, to) = ChicagoClock.DayBounds(new DateOnly(2026, 3, 8));
        Assert.Equal(TimeSpan.FromHours(23), to - from);
        (from, to) = ChicagoClock.DayBounds(new DateOnly(2026, 11, 1));
        Assert.Equal(TimeSpan.FromHours(25), to - from);
        Assert.Equal((Utc(2026, 11, 15, 0), Utc(2026, 11, 16, 0)), new ClinicTime("UTC").DayBounds(new DateOnly(2026, 11, 15)));
    }

    [Fact]
    public void The_zone_must_be_set_outside_Development_and_be_one_the_server_knows()
    {
        Assert.True(ClinicTime.FromSettings(null, null, development: true).IsUtc);
        Assert.Contains("Ehr:Clinic:TimeZone", Assert.Throws<InvalidOperationException>(() => ClinicTime.FromSettings(" ", null, development: false)).Message);
        Assert.Equal(ChicagoId, ClinicTime.FromSettings(ChicagoId, "CST,CDT", development: false).Id);
        Assert.Contains("Nowhere/Invented", Assert.Throws<InvalidOperationException>(() => new ClinicTime("Nowhere/Invented")).Message);
        Assert.Throws<InvalidOperationException>(() => new ClinicTime(ChicagoId, "CST"));
        Assert.Throws<InvalidOperationException>(() => new ClinicTime(ChicagoId, "Central Standard Time,CDT"));
    }
}
