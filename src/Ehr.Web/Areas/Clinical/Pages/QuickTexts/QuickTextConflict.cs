using Ehr.Data;

namespace Ehr.Web.Areas.Clinical.Pages.QuickTexts;

// A save that started from an older version: the person's edit, and what was saved in the meantime.
public record QuickTextConflict(QuickTextForm Mine, QuickText Theirs);
