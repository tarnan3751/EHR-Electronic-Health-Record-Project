using Ehr.Data.Patients;

namespace Ehr.Web.Areas.Clinical.Pages.Patients;

// Rows for _PatientTable: the patients to list, and which of them are on the clinician's panel, marked when the
// list is a search across the clinic.
public sealed record PatientTable(IReadOnlyList<Patient> Patients, IReadOnlySet<Guid> Panel, bool MarkPanel);
