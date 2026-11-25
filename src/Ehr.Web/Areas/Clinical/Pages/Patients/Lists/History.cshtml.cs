using System.Text.Json;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Lists;

// The history of a patient's allergies, medications and diagnoses: every entry added and every change, newest first,
// with who made it and when, from ehr.chart_history, which only the database writes. Read-only, so it works off-site
// like the chart, and open to whoever the chart is open to. One recorded read of the patient. Changes to sensitive
// entries are left out until asked for, which is recorded, as on the chart.
public class HistoryModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass) : PageModel
{
    public const int Limit = 200;

    [BindProperty(SupportsGet = true)]
    public SearchQuery Search { get; set; } = SearchQuery.None;

    [BindProperty(SupportsGet = true)]
    public bool Sensitive { get; set; }

    public ChartHistory History { get; private set; } = null!;

    // For the banner, as on the chart: whether the patient is on the clinician's panel, or until when emergency access lasts.
    public bool OnPanel { get; private set; }

    public DateTimeOffset? EmergencyUntil { get; private set; }

    public static readonly IReadOnlyDictionary<string, string> ListNames = new Dictionary<string, string>
    {
        ["allergy_intolerances"] = "Allergy",
        ["medication_statements"] = "Medication",
        ["conditions"] = "Diagnosis",
    };

    // The fields a change is described by, with the words the forms use for them.
    static readonly (string Column, string Label)[] Fields =
    [
        ("display", "Name"), ("clinical_status", "Status"), ("status", "Status"), ("reaction", "Reaction"), ("severity", "Severity"),
        ("criticality", "Risk"), ("dosage", "How it is taken"), ("effective_start", "Started"), ("effective_end", "Stopped"),
        ("onset_date", "Onset"), ("sensitive", "Sensitive"),
    ];

    public Dictionary<string, string> Route(bool sensitive)
    {
        var route = Search.RouteData();
        route["id"] = History.Patient.Id.ToString();
        if (sensitive)
        {
            route["sensitive"] = "true";
        }

        return route;
    }

    // The entry's name as the chart calls it: "No known allergies" for that entry, whatever name it was stored with.
    public static string Name(ChartChange change)
    {
        var after = JsonDocument.Parse(change.After).RootElement;
        var code = after.TryGetProperty("code", out var value) ? value.GetString() : null;
        return ChartLists.DisplayOf(code, after.GetProperty("display").GetString() ?? "");
    }

    // What the change did, in words: "Added", or each field that changed, from its old value to its new one.
    public static IReadOnlyList<string> Describe(ChartChange change)
    {
        var after = JsonDocument.Parse(change.After).RootElement;
        if (change.Before is null)
        {
            return ["Added"];
        }

        var before = JsonDocument.Parse(change.Before).RootElement;
        return [.. Fields.Where(field => after.TryGetProperty(field.Column, out var now) && before.TryGetProperty(field.Column, out var was) && Text(field.Column, now) != Text(field.Column, was))
            .Select(field => $"{field.Label}: {Text(field.Column, before.GetProperty(field.Column))} → {Text(field.Column, after.GetProperty(field.Column))}")];

        // A stored value in the words the form uses for it ("On hold", "High risk"), not FHIR's code.
        string Text(string column, JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Null => "not recorded",
            JsonValueKind.True => "yes",
            JsonValueKind.False => "no",
            _ => column switch
            {
                "clinical_status" or "status" => ChartListWords.Status(change.TableName, value.ToString()),
                "severity" => ChartListWords.Severity(value.ToString()),
                "criticality" => ChartListWords.Criticality(value.ToString()),
                _ => value.ToString(),
            },
        };
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Response.Headers.CacheControl = "no-store";
        OnPanel = await assignments.IsOnPanelAsync(id, HttpContext.RequestAborted);
        if (!OnPanel)
        {
            EmergencyUntil = await breakGlass.ActiveUntilAsync(id, HttpContext.RequestAborted);
            if (EmergencyUntil is null)
            {
                return RedirectToPage("/Patients/Reason", Search.ChartRouteValues(id));
            }
        }

        if (await phi.ChartHistoryAsync(id, Limit, Sensitive, HttpContext.RequestAborted) is not { } history)
        {
            return NotFound();
        }

        History = history;
        return Page();
    }
}
