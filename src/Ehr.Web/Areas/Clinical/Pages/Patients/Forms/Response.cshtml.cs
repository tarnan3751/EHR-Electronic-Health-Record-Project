using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Ehr.Web.Areas.Clinical.Pages.Patients.Messages;
using Microsoft.AspNetCore.Mvc;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Forms;

// One pre-visit form's answers (the expansion plan's step 13e): each question with the answer chosen and its score, in
// the wording the form was asked with, and the score, band and follow-up the database worked out. A sensitive item:
// opening it is asking to see it, and is recorded as a view of the patient's sensitive items. The inbox's item for an
// answer to review links here. Open to whoever the chart is open to (MessagePage's gate), and, from the step's review,
// to a clinician who holds an open inbox item for this answer, so whoever is told of it can read it without claiming
// emergency access: this one page only, never the chart, the forms list or another form, which keep the gate, and only
// until they mark the item done, after which they reach it as anyone else does (the coordinator's narrowing of the
// step's decision 16). Such a reader is told whose patient this is and that the chart opens only under emergency
// access. It changes nothing.
public class ResponseModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass) : MessagePage(assignments, breakGlass)
{
    public FormAnswers Answers { get; private set; } = null!;

    // Opened only because the reader holds the inbox's open item for it: the chart isn't open to them.
    public bool FromInbox { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid patientId, Guid responseId)
    {
        var refused = await GateAsync(patientId, writing: false);
        FromInbox = refused is not null;
        if (await phi.FormAnswersAsync(patientId, responseId, heldInInbox: FromInbox, HttpContext.RequestAborted) is not { } read)
        {
            return refused ?? NotFound();
        }

        Answers = read;
        return Page();
    }
}
