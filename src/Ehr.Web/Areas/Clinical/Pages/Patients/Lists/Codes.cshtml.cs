using Ehr.Data.Patients;
using Ehr.Domain.Patients;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Lists;

// The reference codes, or the order catalog's items, that match what a clinician has typed, as the options of a
// form's list (js/code-search.js). Reference data, not PHI: nothing about a patient is read or sent, so nothing is recorded. A
// page of the clinical area, so the area's policy covers it: clinicians only.
public class CodesModel(ChartLists lists, Ehr.Domain.Orders.ClinicalOrders orders) : PageModel
{
    // What to offer: each name, and, for a diagnosis, its code beside it.
    public IReadOnlyList<(string Name, string? Label)> Options { get; private set; } = [];

    // kind is a kind of clinical code (condition, medication, allergen) or, for the order form, "order-" and a type of
    // order (order-lab), which searches the order catalog.
    public async Task<IActionResult> OnGetAsync(string? kind, string? q)
    {
        const string order = "order-";
        Options = kind?.StartsWith(order, StringComparison.Ordinal) == true
            ? [.. (await orders.SearchAsync(kind[order.Length..], q, HttpContext.RequestAborted)).Select(item => (item.Display, (string?)null))]
            : [.. (await lists.SearchAsync(kind ?? "", q, HttpContext.RequestAborted))
                .Select(code => (code.Display, code.Kind == ClinicalCodeKind.Condition ? code.Code : null))];
        return Page();
    }
}
