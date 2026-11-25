namespace Ehr.Web.Areas.Clinical.Pages.Schedule;

// The day, time, length and reason fields the booking and rescheduling forms share (_When.cshtml).
public sealed record WhenFields(string? Day, string? Time, int Minutes, string? Reason, IReadOnlyDictionary<string, string> Errors);
