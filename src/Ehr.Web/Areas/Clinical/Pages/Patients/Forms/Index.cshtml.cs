using Ehr.Data.Patients;
using Ehr.Domain.Forms;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Ehr.Domain.Results;
using Ehr.Web.Areas.Clinical.Pages.Patients.Messages;
using Microsoft.AspNetCore.Mvc;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Forms;

// A patient's pre-visit forms (the expansion plan's step 13e): the questionnaires asked for before each appointment,
// grouped by appointment, each with its score, severity band and whether it needs follow-up or has an answer to
// review, and a trend of each questionnaire's scores; and asking for another before a booked appointment. Every form is
// a sensitive item, so the page shows only how many there are until the clinician asks, and the asking is recorded.
// Open to whoever the chart is open to; only a clinician with the patient on their panel asks (MessagePage's gate),
// and the database holds who may and fills in the rest (db/policies/85-pre-visit-forms.sql). Off-site it is read from the
// cloud copy and asking is forwarded to the clinic's server.
public class IndexModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass, PreVisitForms forms) : MessagePage(assignments, breakGlass)
{
    // "Show sensitive items": a link, so the cloud app answers it itself, as a read.
    [BindProperty(SupportsGet = true)]
    public bool Sensitive { get; set; }

    [BindProperty]
    public Guid AppointmentId { get; set; }

    [BindProperty]
    public string? Questionnaire { get; set; }

    // Set by the redirect after asking.
    [BindProperty(SupportsGet = true)]
    public bool Asked { get; set; }

    public PatientForms Forms { get; private set; } = null!;

    public IReadOnlyList<(string Code, string Title)> Questionnaires { get; private set; } = [];

    // One trend for each questionnaire scored at least twice, its follow-up value as the line to stay under.
    public IReadOnlyList<Trend> Trends { get; private set; } = [];

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid patientId) => await LoadAsync(patientId, writing: false) ?? Page();

    public async Task<IActionResult> OnPostAskAsync(Guid patientId)
    {
        if (await LoadAsync(patientId, writing: true) is { } refused)
        {
            return refused;
        }

        if (!Forms.Upcoming.Any(appointment => appointment.Id == AppointmentId) || !Questionnaires.Any(q => q.Code == Questionnaire))
        {
            Error = "Choose a booked appointment and a questionnaire.";
        }
        else
        {
            switch (await forms.AskAsync(patientId, AppointmentId, Questionnaire!, HttpContext.RequestAborted))
            {
                case FormResult.Saved:
                    return RedirectToPage(new { patientId, asked = true, sensitive = Sensitive });
                case FormResult.AlreadyDone:
                    Error = "That questionnaire is already asked for at that appointment.";
                    break;
                default:
                    Error = "The appointment may have changed. Reload the page and try again.";
                    break;
            }
        }

        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return Page();
    }

    async Task<IActionResult?> LoadAsync(Guid patientId, bool writing)
    {
        if (await GateAsync(patientId, writing) is { } refused)
        {
            return refused;
        }

        if (await phi.FormsAsync(patientId, Sensitive, HttpContext.RequestAborted) is not { } read)
        {
            return NotFound();
        }

        Forms = read;
        Questionnaires = await forms.QuestionnairesAsync(HttpContext.RequestAborted);
        Trends = [.. read.Forms.Where(form => form.Response?.Score is not null)
            .GroupBy(form => form.QuestionnaireCode)
            .Where(group => group.Count() >= 2)
            .OrderBy(group => group.First().Title, StringComparer.Ordinal)
            .Select(group =>
            {
                var followUp = group.First().Definition.FollowUpAt;
                var kind = followUp is null ? ReferenceKind.Normal : ReferenceKind.Screening;
                return new Trend(group.Key, $"{group.First().Title}: total score", null, null, followUp,
                    group.Select(form => new TrendPoint(form.Response!.AnsweredAt, form.Response.Score!.Value, null, followUp, kind)), kind);
            })];
        return null;
    }
}
