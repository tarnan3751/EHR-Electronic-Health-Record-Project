using Ehr.Data;
using Ehr.Domain.QuickTexts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Web.Areas.Clinical.Pages.QuickTexts;

// The team's shared phrases. Adding and editing go through htmx: each handler after the first returns only the
// part of the page that changed. Every handler runs in one transaction (DatabaseTransactionFilter), and saves go
// through QuickTextSaver, the same rules the sync API uses.
public class IndexModel(EhrDbContext db, QuickTextSaver saver) : PageModel
{
    public IReadOnlyList<QuickText> QuickTexts { get; private set; } = [];

    public async Task OnGetAsync() =>
        QuickTexts = await db.QuickTexts.AsNoTracking().OrderBy(q => q.Shortcut).ToListAsync(HttpContext.RequestAborted);

    // One phrase as a list row, for Cancel and Keep theirs.
    public async Task<IActionResult> OnGetRowAsync(Guid id) =>
        await FindAsync(id) is { } quickText ? Partial("_QuickText", quickText) : NotFound();

    public async Task<IActionResult> OnGetEditAsync(Guid id) =>
        await FindAsync(id) is { } quickText
            ? Partial("_EditForm", new QuickTextForm
            {
                Id = quickText.Id,
                Version = quickText.Version,
                Shortcut = quickText.Shortcut,
                Body = quickText.Body,
            })
            : NotFound();

    public async Task<IActionResult> OnPostAddAsync(QuickTextForm form)
    {
        if (!ModelState.IsValid)
        {
            return Partial("_AddForm", form);
        }

        var saved = await saver.AddAsync(Guid.CreateVersion7(), form, operationKey: null, HttpContext.RequestAborted);
        switch (saved.Outcome)
        {
            case QuickTextSaveOutcome.Saved:
                // The posted values would otherwise refill the fresh add form.
                ModelState.Clear();
                return Partial("_Added", saved.QuickText);
            case QuickTextSaveOutcome.ShortcutTaken:
                ModelState.AddModelError(nameof(form.Shortcut), $"{saved.QuickText!.Shortcut} is already in use.");
                return Partial("_AddForm", form);
            default:
                throw new InvalidOperationException($"Adding a new phrase can't end {saved.Outcome}.");
        }
    }

    public async Task<IActionResult> OnPostSaveAsync(QuickTextForm form)
    {
        if (!ModelState.IsValid)
        {
            return Partial("_EditForm", form);
        }

        var saved = await saver.UpdateAsync(form.Id, form.Version, form, operationKey: null, HttpContext.RequestAborted);
        switch (saved.Outcome)
        {
            case QuickTextSaveOutcome.Saved:
                return Partial("_QuickText", saved.QuickText);
            case QuickTextSaveOutcome.ChangedSince:
                // Nothing was saved. Show both versions and let the person choose.
                return Partial("_Conflict", new QuickTextConflict(form, saved.QuickText!));
            case QuickTextSaveOutcome.ShortcutTaken:
                ModelState.AddModelError(nameof(form.Shortcut), $"{saved.QuickText!.Shortcut} is already in use.");
                return Partial("_EditForm", form);
            default:
                return NotFound();
        }
    }

    Task<QuickText?> FindAsync(Guid id) =>
        db.QuickTexts.AsNoTracking().SingleOrDefaultAsync(q => q.Id == id, HttpContext.RequestAborted);
}
