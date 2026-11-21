using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Ehr.Web.Areas.Clinical.Pages.Patients.Messages;
using Microsoft.AspNetCore.Mvc;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Forms;

// One pre-visit form's answers (the expansion plan's step 13e): each question with the answer chosen and its score, in
// the wording the form was asked with, and the score, band and follow-up the database worked out. A sensitive item:
// opening it is asking to see it, and is recorded as a view of the patient's sensitive items. The inbox's item for an
// answer to review links here. Open to whoever the chart is open to (MessagePage's gate); it changes nothing.
public class ResponseModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass) : MessagePage(assignments, breakGlass)
{
    public FormAnswers Answers { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(Guid patientId, Guid responseId)
    {
        if (await GateAsync(patientId, writing: false) is { } refused)
        {
            return refused;
        }

        if (await phi.FormAnswersAsync(patientId, responseId, HttpContext.RequestAborted) is not { } read)
        {
            return NotFound();
        }

        Answers = read;
        return Page();
    }
}
