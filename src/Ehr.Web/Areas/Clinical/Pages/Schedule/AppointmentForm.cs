using System.Globalization;
using Ehr.Data.Scheduling;
using Ehr.Domain.Scheduling;

namespace Ehr.Web.Areas.Clinical.Pages.Schedule;

// What the booking and rescheduling forms have in common: a day, a time (the clinic's clock, step 10d; stored in UTC), a length and
// a reason, and what is wrong with them.
public static class AppointmentForm
{
    // The start, or null with the fields' messages added to errors.
    public static DateTimeOffset? Check(Ehr.Domain.Time.ClinicTime clinic, string? day, string? time, int minutes, string? reason, Dictionary<string, string> errors)
    {
        DateTimeOffset? start = null;
        if (!DateOnly.TryParseExact(day?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            errors["Day"] = "Enter the day, as year-month-day.";
        }
        else if (!TimeOnly.TryParseExact(time?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var clock))
        {
            errors["Time"] = "Enter the time, as hours and minutes on the 24-hour clock.";
        }
        else
        {
            // The day and time are the clinic's (step 10d). An hour the clocks skip that day names no moment.
            start = clinic.From(date.ToDateTime(clock));
            if (start is null)
            {
                errors["Time"] = "The clocks go forward that day, so that time doesn't exist. Enter another time.";
            }
            else if (start < DateTimeOffset.UtcNow.AddMinutes(-5))
            {
                errors["Day"] = "Choose a time that hasn't passed.";
            }
            else if (start > DateTimeOffset.UtcNow.AddYears(2))
            {
                errors["Day"] = "Choose a day within the next two years.";
            }
        }

        if (minutes is < AppointmentConfiguration.ShortestMinutes or > AppointmentConfiguration.LongestMinutes)
        {
            errors["Minutes"] = "Choose a length.";
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            errors["Reason"] = "Enter the reason for the visit.";
        }
        else if (reason.Trim().Length > Appointments.MaximumLength)
        {
            errors["Reason"] = $"Shorten the reason to {Appointments.MaximumLength} characters or fewer.";
        }

        return errors.Count == 0 ? start : null;
    }

    // "09:30 to 09:50 CST": the clinic's clock, labeled.
    public static string Time(Ehr.Domain.Time.ClinicTime clinic, AppointmentEntry appointment) =>
        clinic.Range(appointment.StartsAt, appointment.StartsAt.AddMinutes(appointment.Minutes));
}
