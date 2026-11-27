using System.Text;
using System.Text.Json;
using Ehr.Data.Patients;
using Ehr.Domain.Notes;
using Ehr.Testing;
using Xunit;

namespace Ehr.Web.Tests;

public class NoteContentTests
{
    [Fact]
    public void Incomplete_drafts_are_allowed_but_signatures_name_every_missing_field()
    {
        var content = new NoteContent("", " \t\n", "", "");
        Assert.Empty(content.Validate(signing: false));
        Assert.Equal(["Enter Subjective before signing.", "Enter Objective before signing.", "Enter Assessment before signing.", "Enter Plan before signing."],
            content.Validate(signing: true));
    }

    [Fact]
    public void Field_length_null_characters_and_utf8_bytes_are_bounded()
    {
        Assert.Empty(new NoteContent(new string('a', 16000), "Objective", "Assessment", "Plan").Validate(true));
        Assert.Contains("Shorten Subjective", Assert.Single(new NoteContent(new string('a', 16001), "Objective", "Assessment", "Plan").Validate(true)));
        Assert.Contains("Shorten Objective", Assert.Single(new NoteContent("Subjective", new string('a', 16001), "Assessment", "Plan").Validate(true)));
        Assert.Contains("null character", Assert.Single(new NoteContent("Subjective", "Objective\0", "Assessment", "Plan").Validate(true)));
        var unicode = new NoteContent(new string('界', 16000), new string('界', 16000), "Assessment", "Plan");
        Assert.Contains("over the size limit", Assert.Single(unicode.Validate(true)));
    }

    // The draft's Body, which the desktop app keeps and shows as plain text (UC 8): each section under its heading.
    [Fact]
    public void The_body_is_plain_text_with_the_four_headings()
    {
        Assert.Equal("Subjective\nS text\n\nObjective\nO text\n\nAssessment\nA text\n\nPlan\nP text",
            new NoteContent("S text", "O text", "A text", "P text").Body);
    }

    [Fact]
    public void Legacy_body_is_plain_text_and_stale_structured_values_never_replace_it()
    {
        var draft = new ClinicalDraft
        {
            Body = "Synthetic plain text",
            Version = 3,
            StructuredVersion = 2,
            Subjective = "Old subjective",
            Objective = "Old objective",
            Assessment = "Old assessment",
            Plan = "Old plan",
        };
        Assert.Equal(NoteOrigin.Body, NoteContent.OriginOf(draft));
        Assert.Equal(new NoteContent(draft.Body, "", "", ""), NoteContent.From(draft));
        Assert.Equal([new NoteSection(NoteSections.DraftText, draft.Body)], NoteSections.AsSaved(draft));
        Assert.Contains(ClinicalNotes.SigningProblems(draft), p => p.Contains("another window or app", StringComparison.Ordinal));
        draft.StructuredVersion = 3;
        Assert.Equal(new NoteContent("Old subjective", "Old objective", "Old assessment", "Old plan"), NoteContent.From(draft));
        Assert.Empty(ClinicalNotes.SigningProblems(draft));
        draft.HasUnreviewedProposals = true;
        Assert.Contains(ClinicalNotes.SigningProblems(draft), p => p.Contains("AI proposal", StringComparison.Ordinal));
    }

    // Step 14: a draft written before notes had four sections opens with its History in Subjective, and nothing about the
    // draft changes until it is saved, which writes the four sections and clears the History together.
    [Fact]
    public void An_earlier_draft_opens_with_its_history_in_subjective_and_changes_only_when_saved()
    {
        var draft = new ClinicalDraft
        {
            Body = "History\nOld history\n\nAssessment\nOld assessment\n\nPlan\nOld plan",
            Version = 4,
            StructuredVersion = 4,
            History = "Old history",
            Assessment = "Old assessment",
            Plan = "Old plan",
        };
        Assert.Equal(NoteOrigin.History, NoteContent.OriginOf(draft));
        var opened = NoteContent.From(draft);
        Assert.Equal(new NoteContent("Old history", "", "Old assessment", "Old plan"), opened);
        Assert.Equal([new(NoteSections.History, "Old history"), new(NoteSections.Assessment, "Old assessment"), new NoteSection(NoteSections.Plan, "Old plan")],
            NoteSections.AsSaved(draft));
        Assert.Equal(("Old history", (string?)null, (string?)null, 4L), (draft.History, draft.Subjective, draft.Objective, draft.Version));
        Assert.Contains(ClinicalNotes.SigningProblems(draft), p => p.Contains("History section", StringComparison.Ordinal));
        Assert.Contains(ClinicalNotes.SigningProblems(draft), p => p == "Enter Objective before signing.");

        (opened with { Objective = "Blood pressure 126/80." }).ApplyTo(draft);
        Assert.Equal((null, "Old history", "Blood pressure 126/80.", 5L, 5L), (draft.History, draft.Subjective, draft.Objective, draft.Version, draft.StructuredVersion));
        Assert.Equal(NoteOrigin.Sections, NoteContent.OriginOf(draft));
        Assert.Empty(ClinicalNotes.SigningProblems(draft));
    }

    // Saved in four sections, a draft's plain text has 43 bytes of headings: 15 more than a draft written before step 14
    // (History, Assessment and Plan), and 43 more than the desktop app's plain text. Such a draft at the limit goes over
    // it with no text added, and is told so; one whose text wouldn't fit as it was saved is told to shorten the note.
    [Fact]
    public void An_earlier_draft_over_the_limit_only_by_the_four_headings_is_told_so()
    {
        const string AtLimit = "This draft is at the size limit. Shorten it by a few words to save it in four sections.";
        const string Shorten = "Shorten the note. It is over the size limit.";
        // 65,508 bytes of text: under History, Assessment and Plan, with their 28 bytes of headings, exactly 64 KiB.
        var history = new NoteContent(new string('界', 16_000), "", new string('界', 5_836), "");
        Assert.Equal(65_551, Encoding.UTF8.GetByteCount(history.Body));
        Assert.Equal([AtLimit], history.Validate(false, NoteOrigin.History));
        Assert.Equal([Shorten], history.Validate(false));
        Assert.Equal([Shorten], (history with { Plan = "x" }).Validate(false, NoteOrigin.History));
        // 65,536 bytes of text, saved by the desktop app with no headings, sorted into sections before it is saved.
        var body = new NoteContent(new string('界', 16_000), new string('界', 5_845), "x", "");
        Assert.Equal([AtLimit], body.Validate(false, NoteOrigin.Body));
        Assert.Equal([Shorten], (body with { Plan = "x" }).Validate(false, NoteOrigin.Body));
    }

    // What the editor says each section holds (step 14): the dictation note schema's descriptions of its four fields,
    // word for word, as the schema's $comment asks of the editor.
    [Fact]
    public void The_editor_defines_each_section_as_the_note_schema_does()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(RepositoryRoot.Combine("src/Ehr.Dictation/schema/note-fields.schema.json")));
        var properties = schema.RootElement.GetProperty("properties");
        Assert.Equal([.. NoteSections.Definitions.Keys.Select(section => section.ToLowerInvariant())], properties.EnumerateObject().Select(property => property.Name));
        foreach (var (section, definition) in NoteSections.Definitions)
        {
            Assert.Equal(properties.GetProperty(section.ToLowerInvariant()).GetProperty("description").GetString(), definition);
        }
    }

    // A note signed before step 14 is shown as it was signed; one signed in four sections gives an older desktop build
    // its Subjective and Objective under History, so nothing of it is left out there.
    [Fact]
    public void A_signed_note_is_shown_under_the_headings_it_was_signed_with()
    {
        var earlier = new ClinicalNote { History = "H", Assessment = "A", Plan = "P" };
        Assert.Equal(["History", "Assessment", "Plan"], NoteSections.Of(earlier).Select(s => s.Title));
        Assert.Equal("H", NoteSections.HistoryForOlderReaders(earlier));
        var soap = new ClinicalNote { Subjective = "S", Objective = "O", Assessment = "A", Plan = "P" };
        Assert.Equal(["Subjective", "Objective", "Assessment", "Plan"], NoteSections.Of(soap).Select(s => s.Title));
        Assert.Equal("Subjective\nS\n\nObjective\nO", NoteSections.HistoryForOlderReaders(soap));
    }
}
