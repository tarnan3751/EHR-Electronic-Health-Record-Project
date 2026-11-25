using System.Globalization;
using Ehr.Data.Patients;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Lists;

// Adds or changes one entry of a patient's allergies, medications or diagnoses (the expansion plan's step 2; UC 4's
// chart). One page for the three lists, which differ only in their fields. The entry's name is picked from the
// reference codes as the clinician types (js/code-search.js fills the suggestions; without JavaScript the name is
// typed in full), and the form refuses a name that isn't one of them, so every new entry is coded. An entry recorded
// before there were codes keeps its name until it is changed. Nothing is deleted: an entry that no longer applies is
// given another status. The database keeps every change (ehr.chart_history) and records the write.
//
// The order form's rule: only for a patient on the clinician's panel; anyone else, a clinician under emergency access
// included, is sent back to the chart before anything is read. Off-site, the cloud app forwards the post; when it
// can't, its "Not saved" page shows what was typed again (UC 2, ShownIfNotSaved).
[ShownIfNotSaved(nameof(Name), nameof(Reaction), nameof(Dosage))]
public class EntryModel(PhiReader phi, PatientAssignments assignments, ChartLists lists) : PageModel
{
    // The three lists by their name in the address: the table, the kind of reference code, and what to call one entry.
    public static readonly IReadOnlyDictionary<string, (string Table, string Kind, string One, string Label)> Lists =
        new Dictionary<string, (string, string, string, string)>
        {
            ["allergies"] = ("allergy_intolerances", ClinicalCodeKind.Allergen, "allergy", "Substance"),
            ["medications"] = ("medication_statements", ClinicalCodeKind.Medication, "medication", "Medication"),
            ["diagnoses"] = ("conditions", ClinicalCodeKind.Condition, "diagnosis", "Diagnosis"),
        };

    // The search the clinician came from, from the address only: the form's own Name field is the entry's name, and
    // bound as a search criterion it would put the entry's name, a sensitive diagnosis's too, into the chart's address.
    [BindProperty(SupportsGet = true)]
    [FromQuery]
    public SearchQuery Search { get; set; } = SearchQuery.None;

    [BindProperty(SupportsGet = true)]
    public string List { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public Guid? EntryId { get; set; }

    // Set when the clinician came from the chart with sensitive items shown: the way back shows them again. From the
    // address only, never the form: "Sensitive" is the entry's own box.
    [BindProperty(SupportsGet = true, Name = "showSensitive")]
    [FromQuery]
    public bool ShowSensitive { get; set; }

    // For a new entry: its ID, chosen when the form is opened and posted with it, so posting the same form twice (a
    // double click) saves one entry, not two (ChartLists.SaveAsync). A post without one gets a new ID.
    [BindProperty]
    public Guid? NewEntryId { get; set; }

    [BindProperty]
    public string? Name { get; set; }

    [BindProperty]
    public string? Status { get; set; }

    // Allergies.
    [BindProperty]
    public string? Reaction { get; set; }

    [BindProperty]
    public string? Severity { get; set; }

    [BindProperty]
    public string? Criticality { get; set; }

    // Medications.
    [BindProperty]
    public string? Dosage { get; set; }

    [BindProperty]
    public string? Started { get; set; }

    [BindProperty]
    public string? Stopped { get; set; }

    // Diagnoses.
    [BindProperty]
    public string? Onset { get; set; }

    // Medications and diagnoses.
    [BindProperty]
    public bool Sensitive { get; set; }

    // Null only on the page that says a post wasn't saved because the patient left the clinician's panel (OffPanel),
    // which reads nothing about the patient.
    public ChartEntryForm Form { get; private set; } = null!;

    // A diagnosis whose code is in a sensitive range: its "Sensitive" box is shown checked and can't be cleared.
    public bool SensitiveByCode { get; private set; }

    // Set when the post came from a clinician who no longer has the patient on their panel.
    public bool OffPanel { get; private set; }

    // Set when the database refused the write: the "Not saved:" notice above the form, which keeps what was entered.
    public bool Refused { get; private set; }

    public IReadOnlyDictionary<string, string> Errors { get; private set; } = new Dictionary<string, string>();

    // The fields in the order the form shows them.
    static readonly string[] FieldOrder =
        [nameof(Name), nameof(Reaction), nameof(Severity), nameof(Criticality), nameof(Dosage), nameof(Started), nameof(Stopped), nameof(Onset), nameof(Status)];

    // The field that takes the focus: the first with an error; the name on a form with none. None when the database
    // refused the save: its notice takes the focus instead (data-focus-on-load).
    public string? Focused => Refused ? null : FieldOrder.FirstOrDefault(Errors.ContainsKey) ?? (Errors.Count == 0 ? nameof(Name) : null);

    public (string Table, string Kind, string One, string Label) About => Lists[List];

    public bool IsNew => EntryId is null;

    // The statuses a form offers, with the words the chart uses for them.
    public IReadOnlyList<(string Code, string Name)> Statuses => ChartListWords.Statuses(About.Table);

    // What was typed into the free-text fields, for the page that says the post wasn't saved.
    public IReadOnlyList<(string Field, string Value)> Typed =>
        [.. new (string Label, string? Text)[] { (About.Label, Name), ("Reaction", Reaction), ("How it is taken", Dosage) }
            .Where(typed => !string.IsNullOrWhiteSpace(typed.Text)).Select(typed => (typed.Label, typed.Text!))];

    // The way back to the chart: with the search, and with sensitive items shown when they were.
    public Dictionary<string, string> ChartLink
    {
        get
        {
            var route = Search.RouteData();
            route["id"] = Form.Patient.Id.ToString();
            if (ShowSensitive)
            {
                route["sensitive"] = "true";
            }

            return route;
        }
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (await LoadAsync(id) is { } refused)
        {
            return refused;
        }

        if (Form.Allergy is { } allergy)
        {
            (Name, Status, Reaction, Severity, Criticality) = (allergy.Display, allergy.ClinicalStatus, allergy.Reaction, allergy.Severity, allergy.Criticality);
        }
        else if (Form.Medication is { } medication)
        {
            (Name, Status, Dosage, Sensitive) = (medication.Display, medication.Status, medication.Dosage, medication.Sensitive);
            (Started, Stopped) = (Date(medication.EffectiveStart), Date(medication.EffectiveEnd));
        }
        else if (Form.Condition is { } condition)
        {
            (Name, Status, Onset, Sensitive) = (condition.Display, condition.ClinicalStatus, Date(condition.OnsetDate), condition.Sensitive);
            SensitiveByCode = await lists.IsSensitiveDiagnosisAsync(condition.Code, HttpContext.RequestAborted);
        }
        else
        {
            Status = Statuses[0].Code;
            NewEntryId = Guid.CreateVersion7();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        if (await LoadAsync(id) is { } refused)
        {
            return refused;
        }

        // A new entry keeps the ID its form was opened with, through any mistakes, so a repeated post updates it.
        if (IsNew && (NewEntryId is null || NewEntryId == Guid.Empty))
        {
            NewEntryId = Guid.CreateVersion7();
        }

        var errors = new Dictionary<string, string>();
        var name = Name?.Trim() ?? "";
        var current = Form.Allergy?.Display ?? Form.Medication?.Display ?? Form.Condition?.Display;
        // An unchanged name keeps the code it has, or its lack of one; a new name must be one of the reference codes.
        var kept = current is not null && string.Equals(name, current, StringComparison.OrdinalIgnoreCase);
        var code = kept ? null : await lists.FindAsync(About.Kind, name, HttpContext.RequestAborted);
        if (name.Length == 0)
        {
            errors[nameof(Name)] = $"Enter the {About.Label.ToLowerInvariant()}.";
        }
        else if (!kept && code is null)
        {
            errors[nameof(Name)] = $"Choose a {About.Label.ToLowerInvariant()} from the list.";
        }

        if (!Statuses.Any(status => status.Code == Status))
        {
            errors[nameof(Status)] = "Choose a status.";
        }

        foreach (var (field, value) in new[] { (nameof(Reaction), Reaction), (nameof(Dosage), Dosage) })
        {
            if (value?.Trim().Length > ChartLists.MaximumLength)
            {
                errors[field] = $"Shorten this to {ChartLists.MaximumLength} characters or fewer.";
            }
        }

        if (Severity is not null && !AllergySeverity.All.Contains(Severity))
        {
            errors[nameof(Severity)] = "Choose a severity.";
        }

        if (Criticality is not null && !AllergyCriticality.All.Contains(Criticality))
        {
            errors[nameof(Criticality)] = "Choose a risk.";
        }

        var started = Parse(nameof(Started), Started, errors);
        var stopped = Parse(nameof(Stopped), Stopped, errors);
        var onset = Parse(nameof(Onset), Onset, errors);
        if (started is not null && stopped is not null && stopped < started)
        {
            errors[nameof(Stopped)] = "The stop date is before the start date.";
        }

        // A diagnosis sensitive by its code stays sensitive whatever was posted (its box is fixed, and a disabled box
        // isn't sent); the database holds the same rule.
        if (List == "diagnoses")
        {
            SensitiveByCode = code?.Sensitive ?? (kept && await lists.IsSensitiveDiagnosisAsync(Form.Condition?.Code, HttpContext.RequestAborted));
            Sensitive |= SensitiveByCode;
        }

        if (errors.Count > 0)
        {
            Errors = errors;
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            return Page();
        }

        var entryId = EntryId ?? NewEntryId!.Value;
        var saved = List switch
        {
            "allergies" => await lists.SaveAsync(new AllergyIntolerance
            {
                Id = entryId,
                PatientId = id,
                Code = kept ? Form.Allergy!.Code : code!.Code,
                CodeSystem = kept ? Form.Allergy!.CodeSystem : code!.System,
                Display = kept ? current! : code!.Display,
                ClinicalStatus = Status!,
                Category = kept ? Form.Allergy!.Category : code!.Category,
                Criticality = Criticality,
                Reaction = Blank(Reaction),
                Severity = Severity,
                RecordedAt = Form.Allergy?.RecordedAt ?? DateTimeOffset.UtcNow,
            }, IsNew, HttpContext.RequestAborted),
            "medications" => await lists.SaveAsync(new MedicationStatement
            {
                Id = entryId,
                PatientId = id,
                Code = kept ? Form.Medication!.Code : code!.Code,
                Display = kept ? current! : code!.Display,
                Dosage = Blank(Dosage),
                Status = Status!,
                EffectiveStart = started,
                EffectiveEnd = stopped,
                Sensitive = Sensitive,
            }, IsNew, HttpContext.RequestAborted),
            _ => await lists.SaveAsync(new Condition
            {
                Id = entryId,
                PatientId = id,
                Code = kept ? Form.Condition!.Code : code!.Code,
                Display = kept ? current! : code!.Display,
                ClinicalStatus = Status!,
                OnsetDate = onset,
                Sensitive = Sensitive,
            }, IsNew, HttpContext.RequestAborted),
        };
        if (saved != ChartSave.Saved)
        {
            // Refused by the database (row-level security, or the entry isn't this patient's): the form again, with
            // what was entered, and a notice that says nothing was saved.
            Refused = true;
            Response.StatusCode = StatusCodes.Status409Conflict;
            return Page();
        }

        // The chart confirms it ("Saved:"), from the entry as the database now holds it.
        var chart = Search.ChartRouteValues(id);
        chart["saved"] = entryId;
        chart["savedList"] = List;
        if (ShowSensitive)
        {
            chart["sensitive"] = "true";
        }

        return RedirectToPage("/Patients/Chart", chart);
    }

    static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    static string? Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    static DateOnly? Parse(string field, string? value, Dictionary<string, string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            && date <= DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)) && date.Year >= 1900)
        {
            return date;
        }

        errors[field] = "Enter a date that exists and isn't in the future, as year-month-day.";
        return null;
    }

    // The gate, then the one recorded read. Null when the form may be shown. Never cached, opened or posted, as the
    // chart isn't: the form can show a sensitive entry, and it shouldn't stay on a shared computer's disk.
    async Task<IActionResult?> LoadAsync(Guid id)
    {
        Response.Headers.CacheControl = "no-store";
        if (!Lists.ContainsKey(List))
        {
            return NotFound();
        }

        if (!await assignments.IsOnPanelAsync(id, HttpContext.RequestAborted))
        {
            // A post from a form opened while the patient was on the panel: say it wasn't saved, and show what was
            // typed, without reading anything about the patient. Anything else goes back to the chart.
            if (HttpMethods.IsPost(Request.Method))
            {
                OffPanel = true;
                Response.StatusCode = StatusCodes.Status409Conflict;
                return Page();
            }

            return RedirectToPage("/Patients/Chart", Search.ChartRouteValues(id));
        }

        if (await phi.ChartEntryAsync(id, About.Table, EntryId, HttpContext.RequestAborted) is not { } form)
        {
            return NotFound();
        }

        Form = form;
        // An entry that isn't this patient's, or doesn't exist: back to the chart, which lists what does.
        return EntryId is not null && form.Allergy is null && form.Medication is null && form.Condition is null
            ? RedirectToPage("/Patients/Chart", Search.ChartRouteValues(id))
            : null;
    }
}
