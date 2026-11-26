namespace Ehr.Data.Patients;

// An order, or an allergy to add, that a dictation proposes (the expansion plan's step 15; FR-14 as reworded): one row
// for each proposal in its review's stored result, at its place there (Ordinal, into the result's proposals). It holds
// no text: its words are the review's transcript between the offsets the result gives. PHI, with forced row-level
// security and the write-audit trigger (db/policies). Decided once, by the clinician who dictated it: Ordered with the
// order they signed for it, or Recorded with the allergy they added for it, each through its normal form and every check
// of that form (ClinicalOrders, ChartLists); or Dismissed. The database holds a link to an order or allergy made just then
// by that clinician (db/policies/55-ai-review.sql). Nothing turns a proposal into an order or an allergy by itself.
public sealed class AiChartProposal
{
    // The most proposals one dictation gives (the sidecar sends no more).
    public const int MaxPerReview = 20;

    public Guid Id { get; set; }
    public Guid ReviewId { get; set; }
    public Guid PatientId { get; set; }
    public short Ordinal { get; set; }
    public required string Kind { get; set; }
    public required string State { get; set; }
    public Guid? OrderId { get; set; }
    public Guid? AllergyId { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

// What a dictation may propose, as the sidecar names it: an order of any type the order form takes, or an allergy to add.
// Nothing that removes, resolves or changes an allergy, and nothing that stops or changes an order.
public static class AiChartProposalKinds
{
    public const string Order = "order";
    public const string AllergyAddition = "allergy_addition";

    public static readonly string[] All = [Order, AllergyAddition];
}

public static class AiChartProposalStates
{
    public const string Pending = "pending";
    public const string Ordered = "ordered";
    public const string Recorded = "recorded";
    public const string Dismissed = "dismissed";

    public static readonly string[] All = [Pending, Ordered, Recorded, Dismissed];
}
