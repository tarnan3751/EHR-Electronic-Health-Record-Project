using Ehr.Data.Patients;

namespace Ehr.Web.Areas.Clinical.Pages.Patients;

// Rows for _PatientTable: the patients to list, and which of them are on the clinician's panel, marked when the list is
// a search across the clinic: theirs, or restricted, since a chart off the panel asks for a reason first. The search's
// criteria go along in each chart link, so the chart can link back to the same results.
public sealed record PatientTable(IReadOnlyList<Patient> Patients, IReadOnlySet<Guid> Panel, bool MarkPanel, SearchQuery Search);
