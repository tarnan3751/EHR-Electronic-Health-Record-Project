using Ehr.Data.Patients;

namespace Ehr.Domain.Notes;

// One section of a note as a page shows it: its heading and its text, kept as it was typed.
public sealed record NoteSection(string Title, string Text);

// A note's sections (the expansion plan's step 14): Subjective, Objective, Assessment and Plan. A note signed before
// then keeps History, Assessment and Plan, and every read-only view shows a note or a draft under the headings it was
// written with: only the editor puts an earlier draft's History in Subjective, for its author to sort and save.
public static class NoteSections
{
    public const string Subjective = "Subjective";
    public const string Objective = "Objective";
    public const string Assessment = "Assessment";
    public const string Plan = "Plan";

    // The first section of a note or draft written before step 14.
    public const string History = "History";

    // A draft last saved by the desktop app, whose plain text the web hasn't sorted into sections since (UC 8).
    public const string DraftText = "Draft text";

    // A signed note as it was signed.
    public static IReadOnlyList<NoteSection> Of(ClinicalNote note) => note.History is { } history
        ? [new(History, history), new(Assessment, note.Assessment), new(Plan, note.Plan)]
        : [new(Subjective, note.Subjective ?? ""), new(Objective, note.Objective ?? ""), new(Assessment, note.Assessment), new(Plan, note.Plan)];

    // A draft as it was last saved.
    public static IReadOnlyList<NoteSection> AsSaved(ClinicalDraft draft) => NoteContent.OriginOf(draft) switch
    {
        NoteOrigin.Body => [new(DraftText, draft.Body)],
        NoteOrigin.History => [new(History, draft.History ?? ""), new(Assessment, draft.Assessment ?? ""), new(Plan, draft.Plan ?? "")],
        _ => new NoteContent(draft.Subjective ?? "", draft.Objective ?? "", draft.Assessment ?? "", draft.Plan ?? "").Sections,
    };

    // What the first section of a signed note holds for a desktop build that knows only History, Assessment and Plan
    // (DesktopApi): a note signed in four sections gives its Subjective and Objective there, each under its heading, so
    // nothing of it is left out.
    public static string HistoryForOlderReaders(ClinicalNote note) =>
        note.History ?? Render([new(Subjective, note.Subjective ?? ""), new(Objective, note.Objective ?? "")]);

    // The plain text of sections, each heading on a line of its own before its text, a blank line between them: the
    // draft's Body, which the desktop app keeps and shows (UC 8).
    public static string Render(IEnumerable<NoteSection> sections) =>
        string.Join("\n\n", sections.Select(section => $"{section.Title}\n{section.Text}"));
}
