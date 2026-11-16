using System.Text;
using Ehr.Data.Patients;

namespace Ehr.Domain.Notes;

// The editor boundary also accepts future clinician-approved UC 13 values. No review flags come from clients.
public sealed record NoteContent(string History, string Assessment, string Plan)
{
    public const int MaximumFieldLength = 16_000;
    public const int MaximumBodyBytes = 65_536;

    public string Body => $"History\n{History}\n\nAssessment\n{Assessment}\n\nPlan\n{Plan}";

    public static NoteContent From(ClinicalDraft draft) => draft.StructuredVersion == draft.Version
        ? new(draft.History ?? "", draft.Assessment ?? "", draft.Plan ?? "")
        : new(draft.Body, "", "");

    public IReadOnlyList<string> Validate(bool signing)
    {
        var problems = new List<string>();
        foreach (var (name, value) in new[] { ("History", History), ("Assessment", Assessment), ("Plan", Plan) })
        {
            if (signing && string.IsNullOrWhiteSpace(value))
            {
                problems.Add($"Enter {name} before signing.");
            }

            if (value.Length > MaximumFieldLength)
            {
                problems.Add($"Shorten {name} to {MaximumFieldLength:N0} characters or fewer.");
            }

            if (value.Contains('\0'))
            {
                problems.Add($"Remove the null character from {name}.");
            }
        }

        if (Encoding.UTF8.GetByteCount(Body) > MaximumBodyBytes)
        {
            problems.Add("Shorten the note to 64 KiB of UTF-8 text or less.");
        }

        return problems;
    }
}
