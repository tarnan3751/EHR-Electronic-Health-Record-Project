namespace Ehr.Testing;

// One table of cases for reading a recorded visit's proposal and an edit of it as words (the expansion plan's step 16):
// the database's ehr.ambient_words (AmbientSecurityTests) and the app's OfferedWords (OfferedWordsTests) must both find
// each edit the same as the words offered, or not, as the table says. The same: case, punctuation and spacing, the
// characters that show nothing, and compatibility forms. Not the same: another word, and a look-alike letter of another
// alphabet, the known limit. All synthetic.
public static class AmbientWordsCases
{
    public const string Offered = "Your chest is clear.";

    public static readonly (string Name, string Offered, string Edited, bool Same)[] All =
    [
        ("as offered", Offered, "Your chest is clear.", true),
        ("lower case, no full stop", Offered, "your chest is clear", true),
        ("capitals and an exclamation mark", Offered, "YOUR CHEST IS CLEAR!", true),
        ("a comma", Offered, "Your chest, is clear.", true),
        ("tabs, new lines and spaces", Offered, "  Your\tchest\nis   clear. ", true),
        ("a no-break space", Offered, "Your\u00A0chest is clear.", true),
        ("a zero-width space", Offered, "Your che\u200Bst is clear.", true),
        ("a zero-width joiner", Offered, "Your\u200D chest is clear.", true),
        ("a byte order mark", Offered, "\uFEFFYour chest is clear.", true),
        ("a soft hyphen", Offered, "Your ch\u00ADest is clear.", true),
        ("fullwidth letters", Offered, "\uFF39\uFF4F\uFF55\uFF52 \uFF43\uFF48\uFF45\uFF53\uFF54 \uFF49\uFF53 \uFF43\uFF4C\uFF45\uFF41\uFF52\uFF0E", true),
        ("a dash for a space", Offered, "Your chest\u2014is clear.", true),
        ("an ellipsis", Offered, "Your chest is clear\u2026", true),
        ("decomposed accents", "Caf\u00E9 visit, no fever.", "Cafe\u0301 visit, no fever.", true),
        ("a word more", Offered, "Your chest is clear today.", false),
        ("a word changed", Offered, "Your chest is clean.", false),
        ("a negation", Offered, "Your chest is not clear.", false),
        ("a Cyrillic look-alike letter", Offered, "Your chest is \u0441lear.", false),
    ];
}
