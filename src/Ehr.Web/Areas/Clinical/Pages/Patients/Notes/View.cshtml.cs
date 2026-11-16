using Ehr.Data;
using Ehr.Data.Patients;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Notes;

public class ViewModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass, EhrDbContext db)
    : PatientNotePage(phi, assignments, breakGlass)
{
    public string SignerName { get; private set; } = "";

    public ClinicalNote Note { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(Guid patientId, Guid noteId)
    {
        if (await LoadPatientAsync(patientId) is { } denied)
        {
            return denied;
        }

        if (await Phi.NoteAsync(noteId, patientId, HttpContext.RequestAborted) is not { } note)
        {
            return NotFound();
        }

        Note = note;
        SignerName = await db.StaffUsers.Where(u => u.Id == note.SignerId).Select(u => u.DisplayName)
            .SingleAsync(HttpContext.RequestAborted);
        return Page();
    }
}
