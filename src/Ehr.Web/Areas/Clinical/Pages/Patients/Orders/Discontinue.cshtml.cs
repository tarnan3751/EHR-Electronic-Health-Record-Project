using Ehr.Domain.Orders;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Orders;

// UC 1 (Tahseen Arnan): discontinuing an active order. A signed order is never edited or deleted: this records that it
// was stopped, with a required reason, and the database sets who and when and refuses anything else (db/policies/
// 60-orders.sql). The page shows the patient's banner and the order, one recorded read through the PHI reader, and asks
// for the reason. "Discontinue order" saves it, and the chart then lists the order under its discontinued orders.
//
// The order form's rule: only for a patient on the clinician's panel; anyone else is sent back to the chart before
// anything is read. An order that isn't active any more, because someone discontinued or changed it first, isn't
// changed again: a link to it goes back to the chart, and a post says nothing was saved. Off-site, the cloud app
// forwards the post; js/orders.js posts it with fetch, so when that fails the reason stays on screen, and without
// JavaScript the cloud app's "Not saved" page shows it again to copy (UC 2, ShownIfNotSaved).
[ShownIfNotSaved(nameof(Reason))]
public class DiscontinueModel(PhiReader phi, PatientAssignments assignments, ClinicalOrders orders) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public SearchQuery Search { get; set; } = SearchQuery.None;

    [BindProperty]
    public string? Reason { get; set; }

    public OrderForm Form { get; private set; } = null!;

    // The order to discontinue; null once it is no longer active.
    public ActiveOrder? Order => Form.Replaced;

    public string? Error { get; private set; }

    // Set when the order was discontinued or changed by someone else first.
    public bool Gone { get; private set; }

    public Dictionary<string, string> ChartLink
    {
        get
        {
            var route = Search.RouteData();
            route["id"] = Form.Patient.Id.ToString();
            return route;
        }
    }

    public async Task<IActionResult> OnGetAsync(Guid id, Guid orderId) => await LoadAsync(id, orderId) ?? Page();

    public async Task<IActionResult> OnPostAsync(Guid id, Guid orderId)
    {
        Response.Headers.CacheControl = "no-store";
        if (await LoadAsync(id, orderId) is { } refused)
        {
            return refused;
        }

        var reason = Reason?.Trim();
        if (string.IsNullOrEmpty(reason))
        {
            Error = "Reason for discontinuing is required.";
        }
        else if (reason.Length > OrderForms.MaximumReasonLength)
        {
            Error = $"Reason must be {OrderForms.MaximumReasonLength} characters or less.";
        }

        if (Error is not null)
        {
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            return Page();
        }

        if (!await orders.DiscontinueAsync(orderId, id, Order!.Type, reason!, HttpContext.RequestAborted))
        {
            return NoLongerActive();
        }

        var chart = Search.ChartRouteValues(id);
        chart["discontinued"] = orderId;
        var url = Url.Page("/Patients/Chart", chart)!;
        return Request.Headers.ContainsKey("HX-Request") ? new JsonResult(new { url }) : Redirect(url);
    }

    PageResult NoLongerActive()
    {
        Gone = true;
        Response.StatusCode = StatusCodes.Status409Conflict;
        return Page();
    }

    // The gate, then the one recorded read. Null when the page may be shown.
    async Task<IActionResult?> LoadAsync(Guid id, Guid orderId)
    {
        if (!await assignments.IsOnPanelAsync(id, HttpContext.RequestAborted))
        {
            return RedirectToPage("/Patients/Chart", Search.ChartRouteValues(id));
        }

        if (await phi.OrderFormAsync(id, catalogCode: null, replaces: orderId, HttpContext.RequestAborted) is not { } form)
        {
            return NotFound();
        }

        Form = form;
        if (form.Replaced is null)
        {
            return HttpMethods.IsPost(Request.Method) ? NoLongerActive() : RedirectToPage("/Patients/Chart", Search.ChartRouteValues(id));
        }

        return null;
    }
}
