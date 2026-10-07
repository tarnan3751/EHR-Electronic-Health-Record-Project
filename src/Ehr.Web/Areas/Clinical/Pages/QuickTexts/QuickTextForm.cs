using Ehr.Domain.QuickTexts;

namespace Ehr.Web.Areas.Clinical.Pages.QuickTexts;

// What the add and edit forms post: the phrase, with its rules (QuickTextInput), and for an edit, which phrase and
// the version the edit started from.
public class QuickTextForm : QuickTextInput
{
    public Guid Id { get; set; }

    public int Version { get; set; }
}
