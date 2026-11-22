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

    // What each section holds, as the editor says under its heading: the dictation note schema's descriptions word for
    // word (src/Ehr.Dictation/schema/note-fields.schema.json, whose $comment asks the editor to use them; a test checks).
    public static readonly IReadOnlyDictionary<string, string> Definitions = new Dictionary<string, string>
    {
        [Subjective] = "What the patient reports: the reason for the visit, the symptoms and their timing, the relevant past and family history, medications and allergies, and readings they took at home.",
        [Objective] = "What the clinician found: today's vital signs and examination findings, and the results the clinician has already reviewed (labs, ECG, imaging).",
        [Assessment] = "The clinician's impression: diagnoses, differential diagnoses, and how each problem is doing.",
        [Plan] = "What the clinician says happens next: treatment, medication changes, tests, referrals, follow-up and advice.",
    };

    // A visit type's prompt for a section (Reference.NoteTemplate), by the section's heading.
    public static string Prompt(Ehr.Data.Reference.NoteTemplate template, string section) => section switch
    {
        Subjective => template.SubjectivePrompt,
        Objective => template.ObjectivePrompt,
        Assessment => template.AssessmentPrompt,
        _ => template.PlanPrompt,
    };

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
