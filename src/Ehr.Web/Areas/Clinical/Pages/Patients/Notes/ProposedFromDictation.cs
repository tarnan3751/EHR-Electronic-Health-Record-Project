using System.Text.Json;
using Ehr.Data.Patients;
using Ehr.Domain.Phi;
using Ehr.Web.Api.Dictation;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Notes;

// An order, or an allergy to add, that a dictation proposes (the expansion plan's step 15; FR-14 as reworded), as the
// review and the form opened from it show it: the clinician's own words in the review's stored transcript, in the
// sentence they said them in and the one before it, which may name what the words are about ("Add ivabradine 5 mg twice
// daily. Call that in to his pharmacy."), with the words marked; what was heard after it, as the service found it
// (step 15.1: the sentence after its own, and those after that up to another instruction), so a retraction its rules
// don't read is in view; the words each warning points at; when it was dictated; and the proposal's row, with its
// decision. Nothing else is shown of it, and nothing from it fills a field.
public sealed record ProposedFromDictation(
    AiChartProposal Row, Guid DraftId, string Before, string Words, string After, IReadOnlyList<ProposedFromDictation.Warning> Warnings,
    DateTimeOffset DictatedAt)
{
    public sealed record Warning(WarningCode Code, string Said);

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { AllowOutOfOrderMetadataProperties = true };

    // The most of the transcript shown on either side of the words.
    const int Context = 400;

    // Where its words are in the transcript (UTF-16 offsets), for the review to show them there too.
    public int Start { get; init; }

    public int End { get; init; }

    // What was heard after it (step 15.1), as said; empty when nothing follows, or in a result stored before then.
    public string HeardAfter { get; init; } = "";

    public bool IsOrder => Row.Kind == AiChartProposalKinds.Order;

    public bool Waiting => Row.State == AiChartProposalStates.Pending;

    // Its kind, and its decision, in the words the pages show.
    public string KindWords => IsOrder ? "Order" : "Allergy to add";

    public string StateWords => Row.State switch
    {
        AiChartProposalStates.Ordered => "Ordered",
        AiChartProposalStates.Recorded => "Allergy added",
        AiChartProposalStates.Dismissed => "Dismissed",
        _ => "Awaiting review",
    };

    public static string WarningWords(WarningCode code) => code == WarningCode.PossibleCorrection
        ? "You may have corrected this just after it:"
        : "A passage beside it was hard to hear:";

    // The stored result of a review, as the review page reads it.
    public static DictationResult? Result(AiNoteReview review) =>
        review.ResultJson is null ? null : JsonSerializer.Deserialize<DictationResult>(review.ResultJson, Json);

    public static ProposedFromDictation? From(DictatedProposal proposal) =>
        From(proposal.Proposal, proposal.Review, Result(proposal.Review));

    // The row's proposal in the review's stored result, or null when the result has none at its place.
    public static ProposedFromDictation? From(AiChartProposal row, AiNoteReview review, DictationResult? result)
    {
        if (result?.Proposals is not { } proposals || row.Ordinal >= proposals.Count)
        {
            return null;
        }

        var text = result.Transcript.Text;
        var proposal = proposals[row.Ordinal];
        var start = SentenceStart(text, proposal.Start);
        var mark = start;
        while (mark > 0 && char.IsWhiteSpace(text[mark - 1]))
        {
            mark--;
        }

        if (mark > 0)
        {
            start = SentenceStart(text, mark - 1);
        }

        var end = proposal.End;
        while (end < text.Length && !EndsSentence(text, end))
        {
            end++;
        }

        start = Bounded(text, Math.Max(start, proposal.Start - Context));
        end = Bounded(text, Math.Min(Math.Min(end + 1, text.Length), proposal.End + Context));
        return new(row, review.DraftId, text[start..proposal.Start], text[proposal.Start..proposal.End], text[proposal.End..end],
            [.. proposal.Warnings.Select(warning => new Warning(warning.Code, text[warning.Start..warning.End]))], review.CreatedAt)
        {
            Start = proposal.Start,
            End = proposal.End,
            HeardAfter = proposal.HeardAfter is { } after ? text[after.Start..after.End] : "",
        };
    }

    // Where the sentence holding text[at] begins: just after the last sentence mark before it, its spaces skipped.
    static int SentenceStart(string text, int at)
    {
        var start = at;
        while (start > 0 && !EndsSentence(text, start - 1))
        {
            start--;
        }

        while (start < at && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        return start;
    }

    // A sentence ends at "!", "?", a line break, or a full stop not between two digits ("12.5 mg"), as the sidecar reads it.
    static bool EndsSentence(string text, int at) =>
        text[at] is '!' or '?' or '\n'
        || text[at] == '.' && !(at > 0 && char.IsDigit(text[at - 1]) && at + 1 < text.Length && char.IsDigit(text[at + 1]));

    // Never between the two halves of a character outside the Basic Multilingual Plane.
    static int Bounded(string text, int at) => at > 0 && at < text.Length && char.IsLowSurrogate(text[at]) ? at + 1 : at;
}
