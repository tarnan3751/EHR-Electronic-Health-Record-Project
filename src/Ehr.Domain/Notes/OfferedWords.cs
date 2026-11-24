using System.Text;

namespace Ehr.Domain.Notes;

// The words a value reads as, for telling an edit of a recorded visit's proposal from the words it offered once the
// visit's transcript is gone (the expansion plan's step 16): read as the database reads them (ehr.ambient_words in
// db/policies/56-ambient-listening.sql, after 52-visit-summaries.sql), with the code points that show nothing taken out,
// compatibility forms folded (NFKC), case ignored, and every run of anything but letters and digits read as one space.
// A speed bump against putting a proposal's words into the note unchanged, not proof that the clinician rewrote them: a
// look-alike letter of another alphabet still reads as itself. Both are checked against one table of cases
// (AmbientWordsCases in Ehr.Testing).
public static class OfferedWords
{
    public static bool Same(string edited, string offered) => Read(edited) == Read(offered);

    public static string Read(string value)
    {
        var shown = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (!ShowsNothing(rune.Value))
            {
                shown.Append(rune.ToString());
            }
        }

        var folded = shown.ToString().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var words = new StringBuilder(folded.Length);
        var apart = false;
        foreach (var rune in folded.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                if (apart && words.Length > 0)
                {
                    words.Append(' ');
                }

                words.Append(rune.ToString());
                apart = false;
            }
            else
            {
                apart = true;
            }
        }

        return words.ToString();
    }

    // Unicode's default ignorable code points, as 52-visit-summaries.sql lists them.
    static bool ShowsNothing(int c) =>
        c is 0x00AD or 0x034F or 0x061C or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or 0x3164 or 0xFEFF or 0xFFA0
            or >= 0x180B and <= 0x180F or >= 0x200B and <= 0x200F or >= 0x202A and <= 0x202E or >= 0x2060 and <= 0x206F
            or >= 0xFE00 and <= 0xFE0F or >= 0xFFF0 and <= 0xFFF8 or >= 0x1BCA0 and <= 0x1BCA3 or >= 0x1D173 and <= 0x1D17A
            or >= 0xE0000 and <= 0xE0FFF;
}
