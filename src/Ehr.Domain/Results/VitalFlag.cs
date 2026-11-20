using Ehr.Data.Patients;

namespace Ehr.Domain.Results;

// How a vital sign's value stands against the range it was recorded with (the expansion plan's steps 9b and 9d): the
// one rule every page that flags a vital sign uses, so they can't disagree. A normal range flags a value above its
// high "High" and below its low "Low". A screening value, which a child's blood pressure has (the AAP's Table 6), flags
// a value at or above it "Needs follow-up", and never "High" or abnormal: the table is for screening only.
public static class VitalFlag
{
    public const string High = "High";

    public const string Low = "Low";

    public const string FollowUp = "Needs follow-up";

    public static string? Of(decimal? value, decimal? low, decimal? high, string kind) =>
        value is not { } given ? null
        : kind == ReferenceKind.Screening ? (high is { } screening && given >= screening ? FollowUp : null)
        : high is { } top && given > top ? High
        : low is { } bottom && given < bottom ? Low
        : null;

    public static string? Of(Observation value) => Of(value.Value, value.ReferenceLow, value.ReferenceHigh, value.ReferenceKind);
}
