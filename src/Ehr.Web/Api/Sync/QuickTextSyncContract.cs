using System.Text.Json.Serialization;
using Ehr.Data;

namespace Ehr.Web.Api.Sync;

// The JSON that QuickTextSync sends and receives (camelCase on the wire).

public sealed record SyncedQuickText(Guid Id, string Shortcut, string Body, int Version, DateTimeOffset UpdatedAt)
{
    public static SyncedQuickText From(QuickText quickText) =>
        new(quickText.Id, quickText.Shortcut, quickText.Body, quickText.Version, quickText.UpdatedAt);
}

// Watermark is a string: transaction IDs are 64-bit, more than a JSON number holds exactly in JavaScript.
public sealed record PullResponse(string Watermark, IReadOnlyList<SyncedQuickText> QuickTexts);

public sealed record PushRequest(IReadOnlyList<PushOperation>? Operations);

// BaseVersion is the version the change started from, or null for a new phrase. Id is the phrase's, Key the
// operation's: a new one for every change, reused only when sending the same change again.
public sealed record PushOperation(Guid Key, Guid Id, int? BaseVersion, string? Shortcut, string? Body);

// QuickText is the phrase as it's saved now: the one just saved, or for a conflict, the phrase that conflicts.
public sealed record PushResult(
    Guid Key, PushOutcome Outcome, SyncedQuickText? QuickText, IReadOnlyDictionary<string, string[]>? Errors);

public sealed record PushResponse(IReadOnlyList<PushResult> Results);

[JsonConverter(typeof(JsonStringEnumConverter<PushOutcome>))]
public enum PushOutcome
{
    [JsonStringEnumMemberName("applied")]
    Applied,

    // Applied before, under the same key.
    [JsonStringEnumMemberName("duplicate")]
    Duplicate,

    // Not applied: the phrase changed since BaseVersion, or another phrase has the shortcut.
    [JsonStringEnumMemberName("conflict")]
    Conflict,

    [JsonStringEnumMemberName("invalid")]
    Invalid,
}
