using System.Text;
using Ehr.Data.Patients;

namespace Ehr.Domain.Notes;

// Where a draft's text came from when it is opened in the editor (the expansion plan's step 14).
public enum NoteOrigin
{
    // Saved in Subjective, Objective, Assessment and Plan.
    Sections,

    // Saved before step 14, in History, Assessment and Plan: its History comes into Subjective, to be sorted.
    History,

    // Last saved by the desktop app, as plain text (UC 8): all of it comes into Subjective, to be sorted.
    Body,
}

// The editor and UC 13 transfer share this structured-content boundary. No review flags come from clients.
public sealed record NoteContent(string Subjective, string Objective, string Assessment, string Plan)
{
    public const int MaximumFieldLength = 16_000;
    public const int MaximumBodyBytes = 65_536;

    public IReadOnlyList<NoteSection> Sections =>
    [
        new(NoteSections.Subjective, Subjective), new(NoteSections.Objective, Objective),
        new(NoteSections.Assessment, Assessment), new(NoteSections.Plan, Plan),
    ];

    public string Body => NoteSections.Render(Sections);

    public static NoteOrigin OriginOf(ClinicalDraft draft) =>
        draft.StructuredVersion != draft.Version ? NoteOrigin.Body
        : draft.History is not null ? NoteOrigin.History
        : NoteOrigin.Sections;

    // The draft as the editor opens it. Reading it changes nothing: an earlier draft's History, or text the desktop app
    // saved, is put in Subjective here only, and the draft keeps it as it was until its author saves (ApplyTo).
    public static NoteContent From(ClinicalDraft draft) => OriginOf(draft) switch
    {
        NoteOrigin.Body => new(draft.Body, "", "", ""),
        NoteOrigin.History => new(draft.History ?? "", "", draft.Assessment ?? "", draft.Plan ?? ""),
        _ => new(draft.Subjective ?? "", draft.Objective ?? "", draft.Assessment ?? "", draft.Plan ?? ""),
    };

    // Saves the four sections, and clears an earlier draft's History, in the one update of the draft that follows.
    public void ApplyTo(ClinicalDraft draft)
    {
        draft.Subjective = Subjective;
        draft.Objective = Objective;
        draft.Assessment = Assessment;
        draft.Plan = Plan;
        draft.History = null;
        draft.Body = Body;
        draft.StructuredVersion = ++draft.Version;
    }

    // `from` is where the draft being saved came from (OriginOf). Saved in four sections, a draft's plain text has longer
    // headings than one written before step 14 (History, Assessment and Plan) or saved by the desktop app (none), so one
    // at the size limit can go over it with no text added: then the clinician is told that, not to shorten the note.
    public IReadOnlyList<string> Validate(bool signing, NoteOrigin from = NoteOrigin.Sections)
    {
        var problems = new List<string>();
        foreach (var (name, value) in Sections.Select(section => (section.Title, section.Text)))
        {
            if (signing && IsBlank(value))
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

        var bytes = Encoding.UTF8.GetByteCount(Body);
        if (bytes > MaximumBodyBytes)
        {
            var fourHeadings = HeadingBytes(NoteSections.Subjective, NoteSections.Objective, NoteSections.Assessment, NoteSections.Plan);
            var savedHeadings = from switch
            {
                NoteOrigin.History => HeadingBytes(NoteSections.History, NoteSections.Assessment, NoteSections.Plan),
                NoteOrigin.Body => 0,
                _ => fourHeadings,
            };
            problems.Add(bytes - fourHeadings + savedHeadings <= MaximumBodyBytes
                ? "This draft is at the size limit: shorten it by a few words to save it in four sections."
                : "Shorten the note to 64 KiB of UTF-8 text or less.");
        }

        return problems;
    }

    // Whether a section shows nothing, and so isn't signed (step 14): all it holds is whitespace, of every kind Unicode
    // counts as such (Rune.IsWhiteSpace), and the characters that show nothing which step 13a's summary rule strips. The
    // database's signing rule is the same (ehr.note_section_is_blank, db/policies/50-clinical-notes.sql), code point for
    // code point (NoteBlankTests). It only checks: what is stored is what was written.
    public static bool IsBlank(string section) => section.EnumerateRunes().All(rune => Rune.IsWhiteSpace(rune) || ShowsNothing(rune.Value));

    static bool ShowsNothing(int c) =>
        c is 0x00AD or 0x034F or 0x061C or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or (>= 0x180B and <= 0x180F) or (>= 0x200B and <= 0x200F)
            or (>= 0x202A and <= 0x202E) or (>= 0x2060 and <= 0x206F) or 0x3164 or (>= 0xFE00 and <= 0xFE0F) or 0xFEFF or 0xFFA0
            or (>= 0xFFF0 and <= 0xFFF8) or (>= 0x1BCA0 and <= 0x1BCA3) or (>= 0x1D173 and <= 0x1D17A) or (>= 0xE0000 and <= 0xE0FFF);

    // The bytes the headings, and the lines around them, take in the plain text of sections with these titles.
    static int HeadingBytes(params string[] titles) =>
        Encoding.UTF8.GetByteCount(NoteSections.Render(titles.Select(title => new NoteSection(title, ""))));
}
