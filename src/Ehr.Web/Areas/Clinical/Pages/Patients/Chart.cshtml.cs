using Ehr.Data.Audit;
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

    // The allergies to list: every active one but a "No known allergy" entry.
    public IReadOnlyList<AllergyIntolerance> KnownAllergies =>
        [.. Chart.Allergies.Where(a => a.Code != AllergyIntolerance.NoKnownAllergy)];

    // The "No known allergy" entry when nothing else is recorded: not the same as no allergy information at all.
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

    public IReadOnlyList<ClinicalDraft> Drafts { get; private set; } = [];
    public IReadOnlyList<SignedNoteEntry> Notes { get; private set; } = [];

    // The patient's conversations with their care team (FR-18), latest first.
    public IReadOnlyList<Ehr.Domain.Messaging.ThreadEntry> Threads => Chart.Threads;

    public bool OnPanel { get; private set; }

    // Set when the chart is open under emergency access: until when.
    public DateTimeOffset? EmergencyUntil { get; private set; }

    public string? EnrollmentCode { get; private set; }

    // A visit's vital signs as the chart shows them, or null when they weren't taken.
    public string? BloodPressure(Encounter visit) =>
        Chart.VisitVitals.GetValueOrDefault(visit.Id) is { Systolic: { } systolic, Diastolic: { } diastolic } ? $"{systolic:0.#}/{diastolic:0.#} mmHg" : null;

    public string? HeartRate(Encounter visit) =>
        Chart.VisitVitals.GetValueOrDefault(visit.Id)?.HeartRate is { } rate ? $"{rate:0.#}/min" : null;

    public async Task<IActionResult> OnGetAsync(Guid id) => await LoadAsync(id) ?? Page();

    public async Task<IActionResult> OnPostNewNoteAsync(Guid id)
    {
        var draft = await notes.CreateAsync(id, HttpContext.RequestAborted);
        return draft is null ? Forbid() : RedirectToPage("Notes/Edit", new { patientId = id, draftId = draft.Id });
    }

    // Records "No known allergies" for a patient on the clinician's panel with no allergy recorded. The database refuses
    // it otherwise, and the chart then shows the list as it is.
    public async Task<IActionResult> OnPostNoKnownAllergiesAsync(Guid id)
    {
        if (!await assignments.IsOnPanelAsync(id, HttpContext.RequestAborted))
        {
            return RedirectToPage(Search.ChartRouteValues(id));
        }

        await lists.RecordNoKnownAllergiesAsync(id, HttpContext.RequestAborted);
        return Redirect(Url.Page("Chart", Search.ChartRouteValues(id)) + "#chart-allergies");
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

        var chart = await phi.ChartAsync(id, RecentVisits, AuditParts.Chart, HttpContext.RequestAborted, Discontinued, Sensitive);
        if (chart is null)
        {
            return NotFound();
        }

        Chart = chart;
        Notes = chart.Notes;

        if (OnPanel && chart.HasDrafts)
        {
            Drafts = await phi.NoteDraftsAsync(id, HttpContext.RequestAborted);
        }
        return null;
    }
}
