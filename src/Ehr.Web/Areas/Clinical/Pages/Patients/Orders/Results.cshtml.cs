using System.Globalization;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Ehr.Domain.Results;
using Ehr.Web.Areas.Clinical.Pages.Patients.Messages;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Orders;

// The results of one order (the expansion plan's step 7): the tests it reports with their values, flagged when outside
// the reference range and listed first; entering the values the lab reported, for a lab order still open to them; and
// acknowledging the results, which records who and when and clears the inbox item. Open to whoever the chart is open
// to; only a clinician with the patient on their panel files or acknowledges (MessagePage's gate). The database holds
// all of it (db/policies/75-results.sql): this page only asks. Off-site it is read from the cloud copy and its posts
// are forwarded to the clinic's server.
[ShownIfNotSaved(nameof(CollectedAt))]
public class ResultsModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass, LabResults results, Ehr.Domain.Time.ClinicTime clinic) : MessagePage(assignments, breakGlass)
{
    // The values typed, by test code.
    [BindProperty]
    public Dictionary<string, string?> Values { get; set; } = [];

    // When the sample was collected, on the clinic's clock (step 10d), as a datetime-local field sends it.
    [BindProperty]
    public string? CollectedAt { get; set; }

    // What the page showed when "Acknowledge results" was chosen (OrderResults.Seen).
    [BindProperty]
    public string? Seen { get; set; }

    // Set by the redirect after a save: "filed" or "acknowledged".
    [BindProperty(SupportsGet = true)]
    public string? Done { get; set; }

    public OrderResults Order { get; private set; } = null!;

    public IReadOnlyDictionary<string, string> Errors { get; private set; } = new Dictionary<string, string>();

    // Set when a post asked for something the order no longer takes: someone else got there first.
    public bool Stale { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid patientId, Guid orderId)
    {
        // The clinic's clock, which is what the field takes (step 10d).
        CollectedAt ??= clinic.Now.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
        return await LoadAsync(patientId, orderId, writing: false) ?? Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid patientId, Guid orderId)
    {
        if (await LoadAsync(patientId, orderId, writing: true) is { } refused)
        {
            return refused;
        }

        DateTimeOffset? collected = DateTime.TryParseExact(CollectedAt, ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var typed) ? clinic.From(typed) : null;
        // Only the tests the order reports that still wait for a result: anything else typed is ignored.
        var waiting = Order.Tests.Where(test => test.Value is null).Select(test => test.Code).ToHashSet(StringComparer.Ordinal);
        var errors = LabResults.Check(Values.Where(value => waiting.Contains(value.Key)).ToDictionary(), collected, out var values);
        if (collected is null && typed != default && errors.ContainsKey(nameof(CollectedAt)))
        {
            // A time was typed, and the clinic's clocks skip it that day.
            errors[nameof(CollectedAt)] = "That time doesn't exist on that day: the clocks go forward. Enter the time on the clinic's clock.";
        }

        if (errors.Count == 0)
        {
            if (Order.OpenToResults && await results.FileAsync(patientId, orderId, values, collected!.Value, HttpContext.RequestAborted))
            {
                return RedirectToPage(new { patientId, orderId, done = "filed" });
            }

            Stale = true;
            Order = (await phi.OrderResultsAsync(patientId, orderId, HttpContext.RequestAborted))!;
        }

        Errors = errors;
        Response.StatusCode = Stale ? StatusCodes.Status409Conflict : StatusCodes.Status422UnprocessableEntity;
        return Page();
    }

    public async Task<IActionResult> OnPostAcknowledgeAsync(Guid patientId, Guid orderId)
    {
        if (await LoadAsync(patientId, orderId, writing: true) is { } refused)
        {
            return refused;
        }

        // Only what was on the page is acknowledged: if a result arrived since, the page is shown again with it.
        if (Order.HasResults && Order.AcknowledgedAt is null && Seen == Order.Seen
            && await results.AcknowledgeAsync(patientId, orderId, HttpContext.RequestAborted))
        {
            return RedirectToPage(new { patientId, orderId, done = "acknowledged" });
        }

        // Already acknowledged, by this clinician in another tab or by a colleague, or a result arrived since the
        // page was loaded: say so, with how it stands now, which the read above already has unless the save failed.
        Stale = true;
        if (Seen == Order.Seen)
        {
            Order = (await phi.OrderResultsAsync(patientId, orderId, HttpContext.RequestAborted))!;
        }

        Response.StatusCode = StatusCodes.Status409Conflict;
        return Page();
    }

    async Task<IActionResult?> LoadAsync(Guid patientId, Guid orderId, bool writing)
    {
        if (await GateAsync(patientId, writing) is { } refused)
        {
            return refused;
        }

        if (await phi.OrderResultsAsync(patientId, orderId, HttpContext.RequestAborted) is not { } order)
        {
            return NotFound();
        }

        Order = order;
        return null;
    }
}
