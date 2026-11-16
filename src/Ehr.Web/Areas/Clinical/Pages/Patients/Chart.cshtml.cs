using Ehr.Data.Patients;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Patients;

// A patient's chart: demographics, allergies, medications, diagnoses and recent visits, read only for now. A patient
// off the clinician's panel first asks for a reason (Reason, BreakGlass); then the read goes through the PHI reader,
// which records it as one read of the patient, and a patient this clinician may not see is not found, which gives
// nothing away. From here a clinician puts the patient on their panel or takes them off it, and issues the patient a
// portal enrollment code. The search the clinician came from travels along (SearchQuery), for the way back to it.
public class ChartModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass, PatientEnrollments enrollments, Ehr.Domain.Notes.ClinicalNotes notes) : PageModel
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

    public IReadOnlyList<ClinicalDraft> Drafts { get; private set; } = [];
    public IReadOnlyList<ClinicalNote> Notes { get; private set; } = [];

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

    // The gate, then the read. Null when the chart may be shown; otherwise where to send the clinician instead.
    async Task<IActionResult?> LoadAsync(Guid id)
    {
        OnPanel = await assignments.IsOnPanelAsync(id, HttpContext.RequestAborted);
        if (!OnPanel)
        {
            EmergencyUntil = await breakGlass.ActiveUntilAsync(id, HttpContext.RequestAborted);
            if (EmergencyUntil is null)
            {
                return RedirectToPage("Reason", Search.ChartRouteValues(id));
            }
        }

        var chart = await phi.ChartAsync(id, RecentVisits, HttpContext.RequestAborted);
        if (chart is null)
        {
            return NotFound();
        }

        Chart = chart;
        Notes = await phi.NotesAsync(id, HttpContext.RequestAborted);
        if (OnPanel)
        {
            Drafts = await phi.NoteDraftsAsync(id, HttpContext.RequestAborted);
        }
        return null;
    }
}
