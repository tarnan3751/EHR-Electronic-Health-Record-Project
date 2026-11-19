using Ehr.Data.Patients;

namespace Ehr.Domain.Phi;

// A patient's released test results as PhiReader.ResultsAsync returns them, already recorded as one read of the
// patient, newest first (UC6; Simar Rekhi).
public sealed record PatientResults(Patient Patient, IReadOnlyList<Observation> Results)
{
    // The patient's vital signs, newest first, read with the results for the page's trends (step 9).
    public IReadOnlyList<Observation> Vitals { get; init; } = [];
}
