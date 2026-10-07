using Ehr.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ehr.Web.Areas.Clinical.Pages.QuickTexts;

// The team's shared phrases. Adding and editing go through htmx: each handler after the first returns only the
// part of the page that changed. Every handler runs in one transaction (DatabaseTransactionFilter).
public class IndexModel(EhrDbContext db) : PageModel
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

        var quickText = new QuickText
        {
            Id = Guid.CreateVersion7(),
            Shortcut = Normalize(form.Shortcut!),
            Body = form.Body!,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.QuickTexts.Add(quickText);

        try
        {
            await db.SaveChangesAsync(HttpContext.RequestAborted);
        }
        catch (DbUpdateException e) when (IsShortcutTaken(e))
        {
            ModelState.AddModelError(nameof(form.Shortcut), $"{quickText.Shortcut} is already in use.");
            return Partial("_AddForm", form);
        }

        // The posted values would otherwise refill the fresh add form.
        ModelState.Clear();
        return Partial("_Added", quickText);
    }

    public async Task<IActionResult> OnPostSaveAsync(QuickTextForm form)
    {
        if (!ModelState.IsValid)
        {
            return Partial("_EditForm", form);
        }

        var quickText = await db.QuickTexts.FindAsync([form.Id], HttpContext.RequestAborted);
        if (quickText is null)
        {
            return NotFound();
        }

        // Saves only over the version this edit started from: if someone has saved since, the UPDATE matches no
        // row and EF Core throws DbUpdateConcurrencyException.
        db.Entry(quickText).Property(q => q.Version).OriginalValue = form.Version;
        quickText.Shortcut = Normalize(form.Shortcut!);
        quickText.Body = form.Body!;
        quickText.Version = form.Version + 1;
        quickText.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            await db.SaveChangesAsync(HttpContext.RequestAborted);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Nothing was saved. Show both versions and let the person choose.
            await db.Entry(quickText).ReloadAsync(HttpContext.RequestAborted);
            return Partial("_Conflict", new QuickTextConflict(form, quickText));
        }
        catch (DbUpdateException e) when (IsShortcutTaken(e))
        {
            ModelState.AddModelError(nameof(form.Shortcut), $"{quickText.Shortcut} is already in use.");
            return Partial("_EditForm", form);
        }

        return Partial("_QuickText", quickText);
    }

    Task<QuickText?> FindAsync(Guid id) =>
        db.QuickTexts.AsNoTracking().SingleOrDefaultAsync(q => q.Id == id, HttpContext.RequestAborted);

    // Shortcuts are stored in lowercase, so .NAD and .nad are the same shortcut.
    static string Normalize(string shortcut) => shortcut.ToLowerInvariant();

    static bool IsShortcutTaken(DbUpdateException e) =>
        e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ix_quick_texts_shortcut" };
}
