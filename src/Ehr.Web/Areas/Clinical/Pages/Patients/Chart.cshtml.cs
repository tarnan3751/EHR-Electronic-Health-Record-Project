using Ehr.Data.Patients;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Patients;

// A patient's chart: demographics, allergies, medications, diagnoses, recent visits and active orders (UC 1). A patient
// off the clinician's panel first asks for a reason (Reason, BreakGlass); then the read goes through the PHI reader,
// which records it as one read of the patient, and a patient this clinician may not see is not found, which gives
// nothing away. From here a clinician puts the patient on their panel or takes them off it, and issues the patient a
// portal enrollment code. The search the clinician came from travels along (SearchQuery), for the way back to it.
public class ChartModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass, PatientEnrollments enrollments, Ehr.Domain.Notes.ClinicalNotes notes, ChartLists lists) : PageModel
{
    // How many of the latest visits the chart lists.
    public const int RecentVisits = 5;

    [BindProperty(SupportsGet = true)]
    public SearchQuery Search { get; set; } = SearchQuery.None;

    public PatientChart Chart { get; private set; } = null!;

    public Patient Patient => Chart.Patient;

    // The allergies to list: every active one but a "No known allergies" entry.
    public IReadOnlyList<AllergyIntolerance> KnownAllergies =>
        [.. Chart.Allergies.Where(a => a.Code != AllergyIntolerance.NoKnownAllergy)];

    // The "No known allergies" entry when nothing else is recorded: not the same as no allergy information at all.
    public AllergyIntolerance? NoKnownAllergies =>
        KnownAllergies.Count == 0 ? Chart.Allergies.FirstOrDefault(a => a.Code == AllergyIntolerance.NoKnownAllergy) : null;

    // UC 1: the order just signed, from the order form's redirect, when it is among the active orders.
    [BindProperty(SupportsGet = true)]
    public Guid? Ordered { get; set; }

    public Ehr.Domain.Orders.ActiveOrder? SignedOrder => Chart.Orders.FirstOrDefault(order => order.Id == Ordered);

    // UC 1: set by the discontinue page's redirect, for the chart's confirmation, which is shown only when that order
    // is this patient's and discontinued (PatientChart.ConfirmsDiscontinued).
    [BindProperty(SupportsGet = true)]
    public Guid? Discontinued { get; set; }

    // Whether to show the patient's sensitive diagnoses and medications, which the chart otherwise leaves out. Asking
    // is recorded (PhiReader.ChartAsync).
    [BindProperty(SupportsGet = true)]
    public bool Sensitive { get; set; }

    // Set by the entry form's redirect after a save: the entry and its list (Lists/Entry's name for it), for the
    // chart's "Saved:" notice, which is shown only when that entry is this patient's (PatientChart.Saved).
    [BindProperty(SupportsGet = true)]
    public Guid? Saved { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? SavedList { get; set; }

    // Set by this page's own redirect after "Record no known allergies": what happened, for the notice.
    [BindProperty(SupportsGet = true, Name = "noKnownAllergies")]
    public string? NoKnownAllergiesOutcome { get; set; }

    public const string NoKnownAllergiesSaved = "saved";

    public const string NoKnownAllergiesAllergyRecorded = "allergy-recorded";

    public const string NoKnownAllergiesRefused = "refused";

    // The notice after "Record no known allergies", checked against the list as read now, so the address alone can't
    // make the chart claim what isn't so: "saved" only while "No known allergies" is the list's state, "allergy
    // recorded" only while an allergy is.
    public string? NoKnownAllergiesNotice => NoKnownAllergiesOutcome switch
    {
        NoKnownAllergiesSaved when NoKnownAllergies is not null => "saved",
        NoKnownAllergiesAllergyRecorded when KnownAllergies.Count > 0 => "allergy-recorded",
        NoKnownAllergiesRefused => "refused",
        _ => null,
    };

    // The search's criteria, and the sensitive items while they are shown: for a form on the chart that comes back to it.
    public Dictionary<string, string> SensitiveRouteData()
    {
        var route = Search.RouteData();
        if (Chart.SensitiveShown)
        {
            route["sensitive"] = "true";
        }

        return route;
    }

    // The "Saved:" notice's sentence after the name, for an entry that is shown: where it is, or why it isn't listed.
    public static string SavedWhere(SavedEntry saved) => saved.Listed
        ? $" is listed under {ChartListWords.Lists[saved.Table].Heading}."
        : $" is now {ChartListWords.Status(saved.Table, saved.Status).ToLowerInvariant()}, so it is no longer listed.";

    public IReadOnlyList<ClinicalDraft> Drafts { get; private set; } = [];
    public IReadOnlyList<ClinicalNote> Notes { get; private set; } = [];

    // The patient's conversations with their care team (FR-18), latest first.
    public IReadOnlyList<Ehr.Domain.Messaging.ThreadEntry> Threads => Chart.Threads;

    public bool OnPanel { get; private set; }

    // Set when the chart is open under emergency access: until when.
    public DateTimeOffset? EmergencyUntil { get; private set; }

    public string? EnrollmentCode { get; private set; }

    // A visit's vital signs as the chart shows them, or null when they weren't taken.
    public static string? BloodPressure(Encounter visit) =>
        visit is { SystolicMmHg: { } systolic, DiastolicMmHg: { } diastolic } ? $"{systolic}/{diastolic} mmHg" : null;

    public static string? HeartRate(Encounter visit) =>
        visit.HeartRatePerMinute is { } rate ? $"{rate}/min" : null;

    public async Task<IActionResult> OnGetAsync(Guid id) => await LoadAsync(id) ?? Page();

    public async Task<IActionResult> OnPostNewNoteAsync(Guid id)
    {
        var draft = await notes.CreateAsync(id, HttpContext.RequestAborted);
        return draft is null ? Forbid() : RedirectToPage("Notes/Edit", new { patientId = id, draftId = draft.Id });
    }

    // Records "No known allergies" for a patient on the clinician's panel with no allergy recorded. The database refuses
    // it otherwise, and the chart then shows the list as it is, with a notice that says what happened.
    public async Task<IActionResult> OnPostNoKnownAllergiesAsync(Guid id)
    {
        if (!await assignments.IsOnPanelAsync(id, HttpContext.RequestAborted))
        {
            return RedirectToPage(Search.ChartRouteValues(id));
        }

        var outcome = await lists.RecordNoKnownAllergiesAsync(id, HttpContext.RequestAborted) switch
        {
            // A second click finds it recorded already: the same confirmation as the first.
            ChartSave.Saved or ChartSave.AlreadyRecorded => NoKnownAllergiesSaved,
            ChartSave.AllergyRecorded => NoKnownAllergiesAllergyRecorded,
            _ => NoKnownAllergiesRefused,
        };
        var route = Search.ChartRouteValues(id);
        route["noKnownAllergies"] = outcome;
        if (Sensitive)
        {
            route["sensitive"] = "true";
        }

        return RedirectToPage(route);
    }

    public async Task<IActionResult> OnPostAssignAsync(Guid id)
    {
        if (await LoadAsync(id) is { } refused)
        {
            return refused;
        }

        await assignments.AssignAsync(id, HttpContext.RequestAborted);
        return RedirectToPage(Search.ChartRouteValues(id));
    }

    public async Task<IActionResult> OnPostUnassignAsync(Guid id)
    {
        if (await LoadAsync(id) is { } refused)
        {
            return refused;
        }

        await assignments.EndAsync(id, HttpContext.RequestAborted);
        return RedirectToPage(Search.ChartRouteValues(id));
    }

    public async Task<IActionResult> OnPostEnrollAsync(Guid id)
    {
        if (await LoadAsync(id) is { } refused)
        {
            return refused;
        }

        EnrollmentCode = await enrollments.IssueAsync(id, HttpContext.RequestAborted);
        return Page();
    }

    // The gate, then the read. Null when the chart may be shown; otherwise where to send the clinician instead. Never
    // cached, as the note pages aren't, so the chart isn't kept on a shared computer's disk or shown again by Back after
    // signing out; the browser keeps it only while it's open.
    async Task<IActionResult?> LoadAsync(Guid id)
    {
        Response.Headers.CacheControl = "no-store";
        OnPanel = await assignments.IsOnPanelAsync(id, HttpContext.RequestAborted);
        if (!OnPanel)
        {
            EmergencyUntil = await breakGlass.ActiveUntilAsync(id, HttpContext.RequestAborted);
            if (EmergencyUntil is null)
            {
                return RedirectToPage("Reason", Search.ChartRouteValues(id));
            }
        }

        (string, Guid)? saved = Saved is { } entry && SavedList is not null && Lists.EntryModel.Lists.TryGetValue(SavedList, out var list)
            ? (list.Table, entry)
            : null;
        var chart = await phi.ChartAsync(id, RecentVisits, HttpContext.RequestAborted, Discontinued, Sensitive, saved, Ordered);
        if (chart is null)
        {
            return NotFound();
        }

        Chart = chart;
        if (chart.HasNotes)
        {
            Notes = await phi.NotesAsync(id, HttpContext.RequestAborted);
        }

        if (OnPanel && chart.HasDrafts)
        {
            Drafts = await phi.NoteDraftsAsync(id, HttpContext.RequestAborted);
        }
        return null;
    }
}
