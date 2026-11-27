using System.Globalization;
using Ehr.Data.Patients;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Ehr.Domain.Results;
using Ehr.Web.Areas.Clinical.Pages.Patients.Messages;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Vitals;

// Marking readings of one set of vital signs "entered in error" (the expansion plan's step 9b), with a reason. Nothing
// is deleted or edited: the readings stay in the record, marked, and are left out of the flowsheet, the trends, the
// chart and the offline copy; the correct values are recorded as a new set. A blood pressure is one reading, its two
// numbers marked together (step 9c). Only a clinician with the patient on their panel (MessagePage's gate, as for
// recording). Off-site the post is forwarded to the clinic's server.
[ShownIfNotSaved(nameof(Reason))]
public class ErrorModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass, VitalSigns vitals) : MessagePage(assignments, breakGlass)
{
    // The set: the time its readings were taken, UTC, to the second.
    [BindProperty(SupportsGet = true)]
    public string? At { get; set; }

    // The readings chosen, by observation ID.
    [BindProperty]
    public List<Guid> Chosen { get; set; } = [];

    [BindProperty]
    public string? Reason { get; set; }

    public PatientVitals Vitals { get; private set; } = null!;

    // The set's readings that stand, in the flowsheet's order: a blood pressure by its systolic value.
    public IReadOnlyList<Observation> Readings { get; private set; } = [];

    // The diastolic value of each blood pressure reading listed, by reading ID (step 9c).
    public IReadOnlyDictionary<Guid, Observation> Diastolic { get; private set; } = new Dictionary<Guid, Observation>();

    public DateTimeOffset Taken { get; private set; }

    // Whether the set has a body mass index worked out from its weight and height.
    public bool HasBodyMassIndex { get; private set; }

    public IReadOnlyDictionary<string, string> Errors { get; private set; } = new Dictionary<string, string>();

    public async Task<IActionResult> OnGetAsync(Guid patientId) => await LoadAsync(patientId) ?? Page();

    public async Task<IActionResult> OnPostAsync(Guid patientId)
    {
        if (await LoadAsync(patientId) is { } refused)
        {
            return refused;
        }

        var errors = new Dictionary<string, string>();
        // Only readings of this set, which the read above showed to be this patient's.
        var ids = Readings.Where(reading => Chosen.Contains(reading.Id)).Select(reading => reading.Id).ToList();
        var reason = Reason?.Trim();
        if (ids.Count == 0)
        {
            errors["Chosen"] = "Choose at least one reading.";
        }

        if (string.IsNullOrEmpty(reason))
        {
            errors["Reason"] = "Say why, so the record shows it.";
        }
        else if (reason.Length > 500)
        {
            errors["Reason"] = $"The reason can be up to 500 characters. This one has {reason.Length}.";
        }

        if (errors.Count == 0)
        {
            if (await vitals.MarkInErrorAsync(ids, reason!, HttpContext.RequestAborted) == Marked.Done)
            {
                return RedirectToPage("Index", new { patientId, marked = ids.Count });
            }

            errors[""] = "Reload the page and try again.";
        }

        Errors = errors;
        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return Page();
    }

    async Task<IActionResult?> LoadAsync(Guid patientId)
    {
        // Reading the set to mark it is part of writing: emergency access doesn't open this page.
        if (await GateAsync(patientId, writing: true) is { } refused)
        {
            return refused;
        }

        if (!DateTime.TryParseExact(At, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
        {
            return NotFound();
        }

        if (await phi.VitalsAsync(patientId, HttpContext.RequestAborted) is not { } read)
        {
            return NotFound();
        }

        Vitals = read;
        Taken = new DateTimeOffset(at, TimeSpan.Zero);
        // Not the body mass index: it isn't a reading. It follows its weight and height, and the database marks it with
        // either (ticking it beside them made the second update find it already marked, and the whole set was refused).
        // A blood pressure is one reading (step 9c), listed by its systolic: the database marks the diastolic with it.
        Readings = [.. read.Signs.Where(sign => sign.EffectiveAt == Taken && sign.Status == ObservationStatus.Final && sign.Code is not (VitalSign.BodyMassIndex or VitalSign.Diastolic))
            .OrderBy(sign => Array.IndexOf(VitalSign.All, sign.Code))];
        Diastolic = read.Signs.Where(sign => sign.EffectiveAt == Taken && sign.Status == ObservationStatus.Final && sign.Code == VitalSign.Diastolic)
            .ToDictionary(sign => sign.ReadingId!.Value);
        HasBodyMassIndex = read.Signs.Any(sign => sign.EffectiveAt == Taken && sign.Status == ObservationStatus.Final && sign.Code == VitalSign.BodyMassIndex);
        return Readings.Count == 0 ? RedirectToPage("Index", new { patientId }) : null;
    }
}
