using Ehr.Data.Patients;
using Ehr.Domain.Notes;
using Xunit;

namespace Ehr.Web.Tests;

public class NoteContentTests
{
    [Fact]
    public void Incomplete_drafts_are_allowed_but_signatures_name_every_missing_field()
    {
        var content = new NoteContent("", " \t\n", "");
        Assert.Empty(content.Validate(signing: false));
        Assert.Equal(["Enter History before signing.", "Enter Assessment before signing.", "Enter Plan before signing."], content.Validate(signing: true));
    }

    [Fact]
    public void Field_length_null_characters_and_utf8_bytes_are_bounded()
    {
        Assert.Empty(new NoteContent(new string('a', 16000), "Assessment", "Plan").Validate(true));
        Assert.Contains("Shorten History", Assert.Single(new NoteContent(new string('a', 16001), "Assessment", "Plan").Validate(true)));
        Assert.Contains("null character", Assert.Single(new NoteContent("History\0", "Assessment", "Plan").Validate(true)));
        var unicode = new NoteContent(new string('界', 16000), new string('界', 16000), "Plan");
        Assert.Contains("64 KiB", Assert.Single(unicode.Validate(true)));
    }

    [Fact]
    public void Legacy_body_is_plain_text_and_stale_structured_values_never_replace_it()
    {
        var draft = new ClinicalDraft
        {
            Body = "Synthetic plain text",
            Version = 3,
            StructuredVersion = 2,
            History = "Old history",
            Assessment = "Old assessment",
            Plan = "Old plan",
        };
        Assert.Equal(new NoteContent(draft.Body, "", ""), NoteContent.From(draft));
        Assert.Contains(ClinicalNotes.SigningProblems(draft), p => p.Contains("another client", StringComparison.Ordinal));
        draft.StructuredVersion = 3;
        Assert.Equal(new NoteContent("Old history", "Old assessment", "Old plan"), NoteContent.From(draft));
        Assert.Empty(ClinicalNotes.SigningProblems(draft));
        draft.HasUnreviewedProposals = true;
        Assert.Contains(ClinicalNotes.SigningProblems(draft), p => p.Contains("AI proposal", StringComparison.Ordinal));
    }
}
