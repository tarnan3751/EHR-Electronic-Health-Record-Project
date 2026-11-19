using Ehr.Data.Patients;

namespace Ehr.Domain.Phi;

// A patient's released test results as PhiReader.ResultsAsync returns them, already recorded as one read of the
// patient, newest first (UC6; Simar Rekhi), with the vital signs the page charts (step 9).
public sealed record PatientResults(Patient Patient, IReadOnlyList<Observation> Results)
{
    // The patient's vital signs, newest first, up to 600 values, for the page's trends (step 9). They are never in
    // Results, and no number of them hides a result.
    public IReadOnlyList<Observation> Vitals { get; init; } = [];
}
