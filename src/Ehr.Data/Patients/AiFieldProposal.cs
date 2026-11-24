namespace Ehr.Data.Patients;

// One immutable original suggestion per field/result. Its decision may change only once, from pending to a terminal state.
public sealed class AiFieldProposal
{
    public Guid Id { get; set; }
    public Guid ReviewId { get; set; }
    public Guid PatientId { get; set; }
    public required string Field { get; set; }
    public required string OriginalValue { get; set; }
    public required string State { get; set; }
    public string? ApprovedValue { get; set; }
    public Guid? DecisionKey { get; set; }
    public Guid? ReviewerId { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }

    // A proposal from a recorded visit's conversation is a sensitive item, as its review is: the database sets it.
    public bool Sensitive { get; set; }
}

public static class AiProposalStates
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Edited = "edited";
    public const string Rejected = "rejected";
}
