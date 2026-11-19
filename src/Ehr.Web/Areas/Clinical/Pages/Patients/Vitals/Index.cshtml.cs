using System.Globalization;
using Ehr.Data.Patients;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Ehr.Domain.Results;
using Ehr.Web.Areas.Clinical.Pages.Patients.Messages;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Vitals;

// A patient's vital signs (the expansion plan's step 9): the flowsheet, the latest sets side by side; a trend for each
// sign with more than one value; and recording a new set. Open to whoever the chart is open to; only a clinician with
// the patient on their panel records (MessagePage's gate). The database names each sign, gives it its unit and
// reference range, and works out the body mass index (db/policies/75-results.sql). Off-site it is read from the cloud
// copy and the form's post is forwarded to the clinic's server.
[ShownIfNotSaved(
    "Values[8480-6]", "Values[8462-4]", "Values[8867-4]", "Values[8310-5]", "Values[9279-1]", "Values[59408-5]", "Values[29463-7]", "Values[8302-2]",
    nameof(TakenAt))]
public class IndexModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass, VitalSigns vitals, Ehr.Domain.Time.ClinicTime clinic) : MessagePage(assignments, breakGlass)
{
    // How many sets the flowsheet shows side by side, and how many with "Show earlier sets": as many as a trend draws.
    public const int Columns = 6;
    public const int EarlierColumns = Trend.Most;

    // "Show earlier sets" (step 9b): any set a trend draws can be shown, and so corrected.
    [BindProperty(SupportsGet = true)]
    public bool Earlier { get; set; }

    // Whether there are sets beyond the ones shown.
    public bool MoreSets { get; private set; }

    // The values typed, by LOINC code.
    [BindProperty]
    public Dictionary<string, string?> Values { get; set; } = [];

    // When they were taken, on the clinic's clock (step 10d), as a datetime-local field sends it.
    [BindProperty]
    public string? TakenAt { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Saved { get; set; }

    // "Show entries in error" (step 9b): the flowsheet lists them too, struck through.
    [BindProperty(SupportsGet = true)]
    public bool ShowErrors { get; set; }

    // After marking: how many were marked.
    [BindProperty(SupportsGet = true)]
    public int? Marked { get; set; }

    // How many of the patient's vital signs are marked entered in error.
    public int InError { get; private set; }

    // The entries in error at a time, for the flowsheet when they are shown.
    public IEnumerable<Observation> ErrorsAt(string code, DateTimeOffset time) =>
        Vitals.Signs.Where(sign => sign.Code == code && sign.EffectiveAt == time && sign.Status == ObservationStatus.EnteredInError);

    public PatientVitals Vitals { get; private set; } = null!;

    public IReadOnlyDictionary<string, string> Errors { get; private set; } = new Dictionary<string, string>();

    // The latest times a set was taken, newest first, and each sign's value at each.
    public IReadOnlyList<DateTimeOffset> Times { get; private set; } = [];

    public IReadOnlyList<Trend> Trends { get; private set; } = [];

    public Observation? At(string code, DateTimeOffset time) =>
        Vitals.Signs.FirstOrDefault(sign => sign.Code == code && sign.EffectiveAt == time && ObservationStatus.Released.Contains(sign.Status));

    public static bool IsOutside(Observation sign) =>
        (sign.ReferenceLow is { } low && sign.Value < low) || (sign.ReferenceHigh is { } high && sign.Value > high);

    public async Task<IActionResult> OnGetAsync(Guid patientId)
    {
        // The clinic's clock, which is what the field takes (step 10d).
        TakenAt ??= clinic.Now.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
        return await LoadAsync(patientId, writing: false) ?? Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid patientId)
    {
        if (await LoadAsync(patientId, writing: true) is { } refused)
        {
            return refused;
        }

        DateTimeOffset? taken = DateTime.TryParseExact(TakenAt, ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var typed) ? clinic.From(typed) : null;
        var errors = VitalSigns.Check(Values, taken, out var values);
        if (taken is null && typed != default && errors.ContainsKey(nameof(TakenAt)))
        {
            // A time was typed, and the clinic's clocks skip it that day.
            errors[nameof(TakenAt)] = "That time doesn't exist on that day: the clocks go forward. Enter the time on the clinic's clock.";
        }

        if (errors.Count == 0)
        {
            // Correcting a set (step 9b): after a reading is marked entered in error, the set is typed again with the
            // right value. A value that already stands for that moment, the same, is not recorded twice or refused:
            // it is left as it is. A different one is refused by the database, as before.
            var standing = Vitals.Signs.Where(sign => sign.EffectiveAt == taken && sign.Status == ObservationStatus.Final).ToList();
            var fresh = values.Where(value => !standing.Any(sign => sign.Code == value.Code && sign.Value == value.Value)).ToList();
            // A body mass index when a weight or height is new, the other is typed or already stands, and none stands
            // for that moment: a weight corrected alone gets its body mass index back.
            bool Has(string code) => values.Any(value => value.Code == code) || standing.Any(sign => sign.Code == code);
            var newBodyMassIndex = fresh.Any(value => value.Code is VitalSign.Weight or VitalSign.Height) && Has(VitalSign.Weight) && Has(VitalSign.Height)
                && !standing.Any(sign => sign.Code == VitalSign.BodyMassIndex);
            switch (fresh.Count == 0 ? Recorded.AlreadyRecorded : await vitals.RecordAsync(patientId, fresh, taken!.Value, HttpContext.RequestAborted, newBodyMassIndex))
            {
                case Recorded.Saved:
                    return RedirectToPage(new { patientId, saved = true });
                case Recorded.AlreadyRecorded:
                    errors[""] = "Vital signs are already recorded for that time. Check the flowsheet, or change the time.";
                    break;
                default:
                    errors[""] = "The vital signs couldn't be saved. Reload the page and try again.";
                    break;
            }
        }

        Errors = errors;
        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return Page();
    }

    async Task<IActionResult?> LoadAsync(Guid patientId, bool writing)
    {
        if (await GateAsync(patientId, writing) is { } refused)
        {
            return refused;
        }

        if (await phi.VitalsAsync(patientId, HttpContext.RequestAborted) is not { } read)
        {
            return NotFound();
        }

        Vitals = read;
        InError = read.Signs.Count(sign => sign.Status == ObservationStatus.EnteredInError);
        // The sets shown: those with a value that stands, and, when entries in error are shown, theirs too.
        var times = read.Signs.Where(sign => ShowErrors ? sign.Status == ObservationStatus.EnteredInError || ObservationStatus.Released.Contains(sign.Status) : ObservationStatus.Released.Contains(sign.Status))
            .Select(sign => sign.EffectiveAt).Distinct().OrderByDescending(time => time).ToList();
        // With entries in error shown, the earlier sets are too: an entry in error is never out of reach.
        var columns = Earlier || ShowErrors ? EarlierColumns : Columns;
        Times = [.. times.Take(columns)];
        MoreSets = times.Count > columns;
        Trends = Trend.From(read.Signs, VitalSign.All);
        return null;
    }
}
