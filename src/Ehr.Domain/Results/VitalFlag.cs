using Ehr.Data.Patients;

namespace Ehr.Domain.Results;

// How a vital sign's value stands against the range it was recorded with (the expansion plan's step 9b): the one rule
// every page that flags a vital sign uses, so they can't disagree. A value above the range's high is "High", below its
// low "Low".
public static class VitalFlag
{
    public const string High = "High";

    public const string Low = "Low";

    public static string? Of(decimal? value, decimal? low, decimal? high) =>
        value is not { } given ? null
        : high is { } top && given > top ? High
        : low is { } bottom && given < bottom ? Low
        : null;

    public static string? Of(Observation value) => Of(value.Value, value.ReferenceLow, value.ReferenceHigh);
}
