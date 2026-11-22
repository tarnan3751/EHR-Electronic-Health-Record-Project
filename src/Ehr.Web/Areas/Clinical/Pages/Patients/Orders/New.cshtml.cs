using Ehr.Data.Orders;
using Ehr.Domain.Accounts;
using Ehr.Domain.Orders;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Ehr.Domain.Refills;
using Ehr.Web.Accounts;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Orders;

// UC 1 Place an order (Tahseen Arnan). The order form: the patient's name and allergies at the top, one recorded read
// through the PHI reader; the type, picked by link; the item, suggested from the type's catalog as the clinician types
// (a datalist); and the type's details, the required ones marked. "Sign order" checks for missing fields and for an
// active order of the same product (the same item, or another item for the same RxNorm medication), which needs a
// reason; the database checks the duplicate again, under a lock, sets the signer and the time, and records the write. Then the chart confirms the order and lists it. Cancel is a link back to
// the chart, which saves nothing.
//
// Changing an order is this form too (?replaces=<order>): a signed order is never edited, so the form opens filled with
// the active order's item and details, and "Sign changed order" signs a new order and discontinues the old one as
// replaced, in one transaction. The order being replaced isn't a duplicate of its replacement. If someone discontinued
// or changed it first, nothing is saved and the form says so.
//
// Approving a refill request (step 13d) is this form too (?type=medication&refill=<request>), from the request's page: its
// item is fixed, the clinic's catalog item for the medication's own product, with the request beside it and every check
// of an order. "Sign order" signs it and answers the request with it, in one transaction, and the database holds the
// order to that same product. Another strength, form or drug is a new order, written from the chart once the request is
// declined. If someone answered the request first, or its medication was stopped or changed, nothing is saved and the
// form says so. The request's ID is a query a link carries, which a sign-in drops: after one, the clinician is on a new
// order with no request, and approves it from the request's page again. The patient is then told by email, as on a
// decline.
//
// The same rule as the chart's "New note": only for a patient on the clinician's panel. Anyone else, a clinician under
// emergency access included, is sent back to the chart before anything about the patient is read. Off-site, the cloud
// app forwards the post to the on-prem app; js/orders.js posts it with fetch, so when that fails the form stays filled.
// Without JavaScript, the cloud app's "Not saved" page shows the order's fields again to copy (UC 2, ShownIfNotSaved).
[ShownIfNotSaved(nameof(Item), "Details[indication]", "Details[dose]", "Details[route]", "Details[frequency]", "Details[duration]",
    "Details[reason]", "Details[notes]", nameof(DuplicateReason), nameof(ChangeReason), nameof(SafetyReason))]
public class NewModel(PhiReader phi, PatientAssignments assignments, ClinicalOrders orders, RefillRequests refills, IOptions<SiteAddresses> addresses)
    : PageModel
{
    [BindProperty(SupportsGet = true)]
    public SearchQuery Search { get; set; } = SearchQuery.None;

    // One of OrderType, from the query string; anything else shows only the choice of type.
    [BindProperty(SupportsGet = true)]
    public string? Type { get; set; }

    [BindProperty]
    public string? Item { get; set; }

    [BindProperty]
    public Dictionary<string, string?> Details { get; set; } = [];

    [BindProperty]
    public string? DuplicateReason { get; set; }

    // Step 8: why the order is signed although the database found something against it (an allergy, an interaction).
    [BindProperty]
    public string? SafetyReason { get; set; }

    // Set by the form once it has shown the notes that don't stop an order (a moderate or minor interaction): the
    // first "Sign order" brings them up, the second signs. Without it they would never be seen.
    [BindProperty]
    public bool SafetySeen { get; set; }

    // The form is back only to show those notes; nothing needs fixing.
    public bool ShowingNotes { get; private set; }

    // A change: the active order this one replaces, from the query string, and why, which is optional.
    [BindProperty(SupportsGet = true)]
    public Guid? Replaces { get; set; }

    [BindProperty]
    public string? ChangeReason { get; set; }

    // Set when the order to replace was discontinued or changed by someone else first.
    public bool Gone { get; private set; }

    // Step 13d: the refill request the order approves, from the query string.
    [BindProperty(SupportsGet = true)]
    public Guid? Refill { get; set; }

    // Set when the request was answered first, or can no longer be approved: why, to say so.
    public SignRefusal? RefillRefused { get; private set; }

    public OrderForm Form { get; private set; } = null!;

    public IReadOnlyList<OrderCatalogItem> Catalog { get; private set; } = [];

    // What to fix, by field: "Item", a detail's name, or "DuplicateReason".
    public IReadOnlyDictionary<string, string> Errors { get; private set; } = new Dictionary<string, string>();

    public string? KnownType => Type is not null && OrderForms.Fields.ContainsKey(Type) ? Type : null;

    // The way back to the chart, with the search the clinician came from.
    public Dictionary<string, string> ChartLink
    {
        get
        {
            var route = Search.RouteData();
            route["id"] = Form.Patient.Id.ToString();
            return route;
        }
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (await LoadAsync(id, catalogCode: null) is { } refused)
        {
            return refused;
        }

        if (Form.Replaced is { } replaced)
        {
            Item = replaced.Display;
            Details = OrderForms.Values(replaced.Details);
        }

        // The one item that renews the medication asked for: its own product.
        if (Form.Refill is { } refill)
        {
            Item = refill.CatalogItem;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        Response.Headers.CacheControl = "no-store";
        if (KnownType is not { } type)
        {
            return BadRequest();
        }

        var (item, errors) = await orders.CheckAsync(type, Item, Details, HttpContext.RequestAborted);
        if (await LoadAsync(id, item?.Code) is { } refused)
        {
            return refused;
        }

        var reason = string.IsNullOrWhiteSpace(DuplicateReason) ? null : DuplicateReason.Trim();
        if (Form.Duplicates.Count > 0 && reason is null)
        {
            errors["DuplicateReason"] = "Give a reason to order this again, or cancel.";
        }
        else if (reason?.Length > OrderForms.MaximumReasonLength)
        {
            errors["DuplicateReason"] = $"Shorten the reason to {OrderForms.MaximumReasonLength} characters or fewer.";
        }

        // Step 8: what the database knows against this order was read with the form. Signing needs a reason then.
        var safetyReason = string.IsNullOrWhiteSpace(SafetyReason) ? null : SafetyReason.Trim();
        if (Form.Warnings.Any(OrderSafetyText.NeedsReason) && safetyReason is null)
        {
            errors["SafetyReason"] = "Give a reason to sign this order despite the warnings, or cancel.";
        }
        else if (safetyReason?.Length > OrderForms.MaximumReasonLength)
        {
            errors["SafetyReason"] = $"Shorten the reason to {OrderForms.MaximumReasonLength} characters or fewer.";
        }

        var changeReason = string.IsNullOrWhiteSpace(ChangeReason) ? null : ChangeReason.Trim();
        if (changeReason?.Length > OrderForms.MaximumReasonLength)
        {
            errors["ChangeReason"] = $"Shorten the reason to {OrderForms.MaximumReasonLength} characters or fewer.";
        }

        if (Form.Refill is { } asked && !Form.RenewsRefill)
        {
            errors["Item"] = $"It renews the same medication: {asked.CatalogItem}. To order another, decline the request with a message and write a new order from the chart.";
        }

        if (errors.Count > 0)
        {
            return Refused(errors);
        }

        if (Form.Warnings.Count > 0 && !Form.Warnings.Any(OrderSafetyText.NeedsReason) && !SafetySeen)
        {
            ShowingNotes = true;
            return Refused([]);
        }

        var (order, refusal) = await orders.SignAsync(id, item!, OrderForms.Details(type, Details), Form.Duplicates.Count > 0 ? reason : null,
            HttpContext.RequestAborted, Replaces, changeReason, Form.Warnings.Any(OrderSafetyText.NeedsReason) ? safetyReason : null, Refill);
        if (refusal == SignRefusal.Gone)
        {
            return NoLongerActive();
        }

        if (refusal is SignRefusal.Answered or SignRefusal.NotRenewable)
        {
            return RefillNotApproved(refusal.Value);
        }

        if (refusal == SignRefusal.Unsafe)
        {
            // Something against the order appeared after the form was read, such as a medication a colleague just
            // added: the form again, now with the warning and the reason field.
            if (await LoadAsync(id, item!.Code) is { } changed)
            {
                return changed;
            }

            return Refused(new Dictionary<string, string> { ["SafetyReason"] = "Give a reason to sign this order despite the warnings, or cancel." });
        }

        if (order is null)
        {
            // Another order for the same item was signed at the same moment: warn as for any duplicate.
            if (await LoadAsync(id, item!.Code) is { } gone)
            {
                return gone;
            }

            return Refused(new Dictionary<string, string> { ["DuplicateReason"] = "Give a reason to order this again, or cancel." });
        }

        string url;
        if (Refill is { } approved)
        {
            // The link is the configured portal's home, never built from this request (step 6c), and names nothing.
            await refills.NotifyAnsweredAsync(id, Links.Build(addresses.Value.PortalBaseUrl, "/portal"), HttpContext.RequestAborted);
            url = Url.Page("/Patients/Refills/Review", new { patientId = id, requestId = approved, answered = "approved" })!;
        }
        else
        {
            var chart = Search.ChartRouteValues(id);
            chart["ordered"] = order.Id;
            url = Url.Page("/Patients/Chart", chart)!;
        }

        return Request.Headers.ContainsKey("HX-Request") ? new JsonResult(new { url }) : Redirect(url);
    }

    // Nothing saved: the form again, with what was entered and what to fix.
    PageResult Refused(Dictionary<string, string> errors)
    {
        Errors = errors;
        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return Page();
    }

    // Nothing saved, the order included: the refill request was answered first, or can't be approved any more.
    PageResult RefillNotApproved(SignRefusal why)
    {
        RefillRefused = why;
        Response.StatusCode = StatusCodes.Status409Conflict;
        return Page();
    }

    // Nothing saved: the order to change was discontinued or changed first. What was entered stays, to copy.
    PageResult NoLongerActive()
    {
        Gone = true;
        Response.StatusCode = StatusCodes.Status409Conflict;
        return Page();
    }

    // The gate, then the one recorded read. Null when the form may be shown.
    async Task<IActionResult?> LoadAsync(Guid id, string? catalogCode)
    {
        if (!await assignments.IsOnPanelAsync(id, HttpContext.RequestAborted))
        {
            return RedirectToPage("/Patients/Chart", Search.ChartRouteValues(id));
        }

        // A refill is approved by a medication order, which replaces none.
        if (Refill is not null && (Replaces is not null || (Type ?? OrderType.Medication) != OrderType.Medication))
        {
            return BadRequest();
        }

        if (await phi.OrderFormAsync(id, catalogCode, Replaces, HttpContext.RequestAborted, Refill) is not { } form)
        {
            return NotFound();
        }

        Form = form;
        if (Refill is { } refill)
        {
            Type = OrderType.Medication;
            if (form.Refill is null)
            {
                // Not a request of this patient's: nothing to approve.
                return HttpMethods.IsPost(Request.Method) ? NotFound() : RedirectToPage("/Patients/Chart", Search.ChartRouteValues(id));
            }

            if (!form.Refill.Renewable)
            {
                // Answered already, or its medication no longer active: a post says so; a link goes back to the request.
                return HttpMethods.IsPost(Request.Method)
                    ? RefillNotApproved(form.Refill.Waiting ? SignRefusal.NotRenewable : SignRefusal.Answered)
                    : RedirectToPage("/Patients/Refills/Review", new { patientId = id, requestId = refill });
            }
        }
        if (Replaces is not null && form.Replaced is null)
        {
            // Not an active order of this patient: nothing to change. A post says so; a link goes back to the chart.
            return HttpMethods.IsPost(Request.Method) ? NoLongerActive() : RedirectToPage("/Patients/Chart", Search.ChartRouteValues(id));
        }

        if (form.Replaced is { } replaced && replaced.Type != Type)
        {
            // A change keeps the order's type. A link with another type opens the right form; a post is refused.
            if (HttpMethods.IsPost(Request.Method))
            {
                return BadRequest();
            }

            Type = replaced.Type;
        }

        Catalog = KnownType is { } type ? await orders.CatalogAsync(type, HttpContext.RequestAborted) : [];
        return null;
    }
}
